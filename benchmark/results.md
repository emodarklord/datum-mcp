# Benchmark results

30 single-part tasks in `tasks.json`, from a plain block to a V-pulley and a keyed shaft. Each has an expected volume and box worked out by hand.

A part passes when the saved file has one body, its volume is within 0.5% and each box size is within 0.5 mm. `Datum.Benchmark score` measures that in SOLIDWORKS, so a result doesn't depend on what a session says it did.

## Run of 2026-11-07

SOLIDWORKS 2026 SP4.1 Student, Claude Code subagents, Datum tools only.

| Model | Passed | How it ran | Time | Tool calls | Tokens |
|---|---|---|---|---|---|
| Sonnet | 30/30 | A fresh session per part | median 29 s per part | median 11 per part | median 60,006 per part |
| Haiku | 30/30 | One session for all 30 | 9.3 min in total | 274 in total | 134,394 in total |
| Opus | 30/30 | One session for all 30 | 7.0 min in total | 278 in total | 118,906 in total |

## How to read this

- All three models passed everything, so this set doesn't separate them. It shows that the tools can do these parts, not which model is best.
- These are single parts with one clear answer. Assemblies, drawings and vague requests aren't tested here.
- Sonnet's row can't be compared directly with the others: a session can learn from earlier parts in a shared run.
- Tokens include Claude Code's own instructions and tool lists. That is why a one-feature block used about 59,000 tokens.
- Sonnet first scored 29/30. The miss was an error in the answer key, not in the part: the keyway removes the top of the shaft, so the height is 29.46, not 30. The key was corrected and the same part then passed.
- Sessions had to recover from real problems, which didn't count against them. Two bugs they hit are fixed: a through cut on the far side of a sketch, and a second rectangle on the same centre.
- These runs used a Datum build from before those fixes.
- No other MCP servers were compared.

## Run it yourself

```bash
dotnet run --project benchmark/Datum.Benchmark -c Release -- run benchmark/tasks.json <folder> <model> --claude <claude.exe> --datum <datum.exe>
```

`run` needs Claude Code to be logged in. `score` works on any folder of `<id>.SLDPRT` files, however they were made. The `run` mode has been tried only as far as the login step, because the runs above used subagents.
