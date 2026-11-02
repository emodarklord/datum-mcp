// Every live test drives the same SOLIDWORKS session, and tools default to the active
// document, so running them side by side lets one test close another's document.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
