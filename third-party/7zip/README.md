# 7-Zip libraries

`7z64.dll` (x64) and `7zArm64.dll` (ARM64) are the 7-Zip 26.00 engine, unmodified, from https://www.7-zip.org (GNU LGPL, with the
unRAR restriction). `Leviathan.Providers.Archive` copies them beside every program that uses it; `SevenZipArchiveReader` loads the one
for the running architecture. To update: replace both files with the same version of 7-Zip's `7z.dll` for each architecture and run
the archive tests (`tests/Leviathan.Providers.Archive.Tests`).
