using System.Text.RegularExpressions;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int FirstBomRow = 1;

    public TitleChange SetTitleField(string id, string name, string value) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var links = SheetFormatNotes(drawing).Select(note => note.PropertyLinkedText).ToList();
        var fromModel = links.Any(link => link.Contains($"$PRPSHEET:\"{name}\"", StringComparison.OrdinalIgnoreCase));
        var fromDrawing = links.Any(link => link.Contains($"$PRP:\"{name}\"", StringComparison.OrdinalIgnoreCase));
        if (!fromModel && !fromDrawing)
        {
            var known = links.SelectMany(link => FieldName().Matches(link).Select(m => m.Groups[1].Value)).Where(field => !field.StartsWith("SW-", StringComparison.Ordinal)).Distinct();
            throw new DatumException(ErrorCode.NotFound, $"the title block has no field '{name}'", $"Fields: {string.Join(", ", known)}");
        }

        var model = ViewsOf(document).Select(view => view.ReferencedDocument).OfType<IModelDoc2>().FirstOrDefault();
        var target = fromDrawing ? document : model
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{name} comes from the model, and this drawing has no views yet", "Add a view first");
        var manager = target.Extension.get_CustomPropertyManager("");
        manager.Add3(name, (int)swCustomInfoType_e.swCustomInfoText, value, (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
        if (!fromDrawing)
        {
            // As with a tolerance, a change made from the drawing doesn't mark the model as changed.
            target.SetSaveFlag();
        }

        document.EditRebuild3();
        document.GraphicsRedraw2();
        return new TitleChange(
            name,
            value,
            SheetFormatNotes(drawing).Any(note => note.GetText().Contains(value, StringComparison.OrdinalIgnoreCase)),
            fromDrawing ? null : target.GetPathName());
    });

    public IReadOnlyList<BomRow> AddBom(string id, string view, double x, double y, string? bomTemplate) => Call(() =>
    {
        var document = Find(id);
        var target = ViewOf(id, document, view);
        if (target.ReferencedDocument is not IAssemblyDoc)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{view} doesn't show an assembly", "A BOM needs a view of an assembly");
        }

        var file = bomTemplate ?? Path.Combine(Path.GetDirectoryName(App.GetExecutablePath())!, "lang", "english", "bom-standard.sldbomtbt");
        if (!File.Exists(file))
        {
            file = Path.Combine(App.GetExecutablePath(), "lang", "english", "bom-standard.sldbomtbt");
        }

        if (target.InsertBomTable4(
                false, x / Mm, y / Mm, (int)swBOMConfigurationAnchorType_e.swBOMConfigurationAnchor_TopLeft, (int)swBomType_e.swBomType_PartsOnly, target.ReferencedConfiguration, file, false, 0, false) is null)
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't add a BOM from '{file}'", "Pass a .sldbomtbt template");
        }

        document.EditRebuild3();
        return BomRowsOf(document) ?? [];
    });

    public IReadOnlyList<BomRow> ReadBomTable(string id) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        return BomRowsOf(document) ?? throw new DatumException(ErrorCode.NotFound, "this drawing has no BOM table", "Call bom_add");
    });

    public IReadOnlyList<BalloonInfo> AddBalloons(string id, string view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var target = ViewOf(id, document, view);
        document.ClearSelection2(true);
        document.Extension.SelectByID2(target.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
        var options = drawing.CreateAutoBalloonOptions();
        options.IgnoreMultiple = true;
        options.Layout = (int)swBalloonLayoutType_e.swDetailingBalloonLayout_Square;
        options.Style = (int)swBalloonStyle_e.swBS_Circular;
        options.Size = (int)swBalloonFit_e.swBF_2Chars;
        options.UpperTextContent = (int)swBalloonTextContent_e.swBalloonTextItemNumber;
        var made = drawing.AutoBalloon5(options) as object[];
        document.ClearSelection2(true);
        document.EditRebuild3();
        if (made is not { Length: > 0 })
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS made no balloons in {view}", "Add a BOM first, and check the view shows the assembly's components");
        }

        return BalloonsOf(document);
    });

    public IReadOnlyList<ExplodeLine> AddExplodeLines(string id) => Call<IReadOnlyList<ExplodeLine>>(() =>
    {
        var document = Find(id);
        var assembly = AssemblyOf(document, id);
        var configuration = (IConfiguration)document.GetActiveConfiguration();
        var count = configuration.GetNumberOfExplodeSteps();
        if (count == 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"configuration {configuration.Name} has no exploded view", "Call explode_create first");
        }

        // Component boxes follow the explode, and the lines have to start from where each part sits assembled.
        var exploded = document.Extension.IsExploded(out _);
        var name = Strings(assembly.GetExplodedViewNames2(configuration.Name) as object)[0];
        if (exploded)
        {
            assembly.ShowExploded2(false, name);
        }

        var moved = new Dictionary<string, Point3>(StringComparer.OrdinalIgnoreCase);
        var lines = Enumerable.Range(0, count).Select(i => (IExplodeStep)configuration.GetExplodeStep(i)).SelectMany(step => ExplodePath(step, moved)).ToList();
        var sketches = document.SketchManager;
        sketches.InsertExplodeLineSketch();
        var addToDb = sketches.AddToDB;
        sketches.AddToDB = true;
        try
        {
            foreach (var line in lines)
            {
                sketches.CreateLine(line.From.X, line.From.Y, line.From.Z, line.To.X, line.To.Y, line.To.Z);
            }
        }
        finally
        {
            sketches.AddToDB = addToDb;
        }

        sketches.Insert3DSketch(true);
        document.ClearSelection2(true);
        if (exploded)
        {
            assembly.ShowExploded2(true, name);
        }

        document.EditRebuild3();
        return [.. lines.Select(line => new ExplodeLine(line.Component, Scaled(line.From, Mm), Scaled(line.To, Mm)))];
    });

    private static Point3 Scaled(Point3 point, double by) => new(point.X * by, point.Y * by, point.Z * by);

    // Each moved component's centre, from where earlier steps left it to where this step carries it. Metres, assembly
    // coordinates, with the assembly collapsed.
    private static IEnumerable<ExplodeLine> ExplodePath(IExplodeStep step, Dictionary<string, Point3> moved)
    {
        step.GetExplodeDirection(out var axis);
        var direction = axis switch
        {
            (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_XAxis => new Point3(1, 0, 0),
            (int)swExplodeDirectionIndex_e.swExplodeDirectionIndex_YAxis => new Point3(0, 1, 0),
            _ => new Point3(0, 0, 1),
        };
        var sign = step.ReverseTranslationDirection ? -1 : 1;
        var travel = step.ExplodeDistance * sign;
        foreach (var component in (step.GetComponents() as object[] ?? []).OfType<IComponent2>())
        {
            if (component.GetBox(false, false) is double[] { Length: 6 } box)
            {
                var from = moved.TryGetValue(component.Name2, out var earlier) ? earlier : new Point3((box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2);
                var to = new Point3(from.X + (direction.X * travel), from.Y + (direction.Y * travel), from.Z + (direction.Z * travel));
                moved[component.Name2] = to;
                yield return new ExplodeLine(component.Name2, from, to);
            }
        }
    }

    private static List<INote> SheetFormatNotes(IDrawingDoc drawing) =>
        [.. ((drawing.GetFirstView() as IView)?.GetNotes() as object[] ?? []).OfType<INote>()];

    private static ITableAnnotation? BomTableOf(IModelDoc2 document)
    {
        var tables = ViewsOf(document).Concat(((IDrawingDoc)document).GetFirstView() is IView sheet ? [sheet] : [])
            .SelectMany(view => (view.GetTableAnnotations() as object[] ?? []).OfType<ITableAnnotation>());
        return tables.FirstOrDefault(table => table.Type == (int)swTableAnnotationType_e.swTableAnnotation_BillOfMaterials);
    }

    private static List<BomRow>? BomRowsOf(IModelDoc2 document)
    {
        if (BomTableOf(document) is not { } table)
        {
            return null;
        }

        var headers = Enumerable.Range(0, table.ColumnCount).Select(column => table.get_Text(0, column)).ToList();
        int Column(string part) => headers.FindIndex(header => header.Contains(part, StringComparison.OrdinalIgnoreCase));
        var (item, number, description, quantity) = (Column("ITEM"), Column("PART"), Column("DESCRIPTION"), Column("QTY"));
        return Enumerable.Range(FirstBomRow, table.RowCount - FirstBomRow).Select(row => new BomRow(
            item < 0 ? "" : table.get_Text(row, item),
            number < 0 ? "" : table.get_Text(row, number),
            description < 0 ? "" : table.get_Text(row, description),
            quantity >= 0 && int.TryParse(table.get_Text(row, quantity), out var count) ? count : 0)).ToList();
    }

    private static SheetBox? BomBoxOf(IModelDoc2 document)
    {
        if (BomTableOf(document) is not { } table || table.GetAnnotation() is not IAnnotation annotation || annotation.GetPosition() is not double[] { Length: >= 2 } at)
        {
            return null;
        }

        var width = Enumerable.Range(0, table.ColumnCount).Sum(column => table.GetColumnWidth(column));
        var height = Enumerable.Range(0, table.RowCount).Sum(row => table.GetRowHeight(row));
        return new SheetBox(at[0] * Mm, (at[1] - height) * Mm, (at[0] + width) * Mm, at[1] * Mm);
    }

    private static List<BalloonInfo> BalloonsOf(IModelDoc2 document) =>
    [
        .. ViewsOf(document).SelectMany(view => (view.GetAnnotations() as object[] ?? []).OfType<IAnnotation>())
            .Select(annotation => (Annotation: annotation, Note: annotation.GetSpecificAnnotation() as INote))
            .Where(pair => pair.Note is not null && pair.Note.IsBomBalloon())
            .Select(pair => new BalloonInfo(
                pair.Note!.GetText(),
                (pair.Annotation.GetAttachedEntities3() as object[] ?? []).OfType<IEntity>().Select(entity => entity.GetComponent() as IComponent2).OfType<IComponent2>().FirstOrDefault()?.Name2,
                pair.Annotation.IsDangling())),
    ];

    [GeneratedRegex("\\$PRP(?:SHEET)?:\"([^\"]+)\"")]
    private static partial Regex FieldName();
}
