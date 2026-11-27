// Every SQLite store test clears the connection pools when it is disposed; run in parallel, one test's cleanup disposes the
// connection another is using ("Cannot access a disposed object: SQLitePCL.sqlite3"). The whole project takes a few seconds, so it runs one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
