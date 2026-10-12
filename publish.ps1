# Builds publish\HddLabelAgent.exe: one file to copy to a shop's computer. It needs no .NET installed there.
$ErrorActionPreference = 'Stop'

dotnet publish "$PSScriptRoot\HddLabelAgent\HddLabelAgent.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o "$PSScriptRoot\publish"
