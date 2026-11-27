---
trigger: always_on
---

# Publish Executable After Task

Whenever you finish a coding task that modifies the `AntigravityQuotaTray` application:
1. Run `dotnet test AntigravityQuotaTray.sln --verbosity minimal` and ensure all tests pass.
2. Stop any running `AntigravityQuotaTray` process so the binary is not locked.
3. Publish the Release build to the `publish` folder in the repository root:
   `dotnet publish AntigravityQuotaTray\AntigravityQuotaTray.csproj -c Release -r win-x64 -o publish --verbosity minimal`
4. Restart `publish\AntigravityQuotaTray.exe` in the background if it was running (or if the user expects the tray app to stay live).
