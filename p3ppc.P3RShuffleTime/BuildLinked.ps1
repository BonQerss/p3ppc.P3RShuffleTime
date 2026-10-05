# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/p3ppc.P3RShuffleTime/*" -Force -Recurse
dotnet publish "./p3ppc.P3RShuffleTime.csproj" -c Release -o "$env:RELOADEDIIMODS/p3ppc.P3RShuffleTime" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location