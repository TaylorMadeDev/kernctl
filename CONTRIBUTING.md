# Contributing to kernctl

Thank you for helping build kernctl. Open an issue before beginning a large behavioural
or architectural change.

## Development workflow

1. Inspect `git status` and preserve unrelated changes.
2. Create a focused branch.
3. Keep presentation logic in view models and platform logic behind core contracts.
4. Add or update tests for state transitions and non-trivial behaviour.
5. Run:

   ```powershell
   dotnet format Kernctl.sln --verify-no-changes
   dotnet build Kernctl.sln --configuration Release
   dotnet test Kernctl.sln --configuration Release --no-build
   ```

6. Update documentation and submit a focused pull request.

Never add destructive system behaviour without a design covering detection,
explanation, privilege, verification, recovery, and user-visible rollback.
