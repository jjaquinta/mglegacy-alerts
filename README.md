# Dev: just run
dotnet run

# Release: single self-contained .exe (~60 MB, no .NET install needed)
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# compressed
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true