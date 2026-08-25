# Contributing to Telltale

Thank you for considering contributing to Telltale. This document covers how to report issues, suggest features, and submit pull requests.

## Bug reports

Open a GitHub issue with:

- A clear title describing the problem
- Steps to reproduce the issue
- What you expected to happen and what actually happened
- Your Windows version and .NET SDK version
- Any relevant log output or screenshots

## Feature requests

Open a GitHub issue describing the feature you'd like to see, why it would be useful, and how you envision it working. If you're unsure whether something fits the project, open an issue to discuss it before starting work.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your changes. Keep commits focused on a single concern.
3. Make sure all tests pass before submitting (see below).
4. Open a pull request against `main` with a clear description of what the change does and why.
5. Link any related issues in the PR description.

### Code style

- **C#**: follow the conventions already in the codebase. The projects target .NET 10 with nullable reference types enabled. `host/` targets `net10.0-windows` because it uses Windows Forms for the notification area icon.
- **TypeScript/React**: follow the existing patterns in `frontend/src/`. Use TypeScript strict mode.
- **Batch files**: they are checked out with CRLF endings, which `.gitattributes` enforces. `cmd` resolves `call :label` by seeking through the file, and an LF file fails at any label far enough in with a message that looks like a typo rather than an encoding problem.
- Keep changes focused. If you spot an unrelated issue while working, open a separate PR for it.

### Testing

All pull requests should pass the existing tests and include tests for new behaviour where practical.

**Backend tests** (from the repo root):

```bash
dotnet test Telltale.slnx
```

That covers three test projects: the recorder, the API, and the application that composes them. The frontend has its own, below.

**Frontend tests**:

```bash
cd frontend
npm test
```

Note that the collector tests exercise Windows-specific P/Invoke code, and the application tests build a Windows Forms project, so both require a Windows machine. The frontend tests run on any platform.

## Running the application

`dev.bat` runs the recorder and the API as separate console applications with the Vite dev server in front of them, which is the fastest loop to work in and shows you what each half is doing.

To run what actually ships, build it with `publish.bat` and run `publish/Telltale.exe`. It records into your real capture database, so point `databasePath` in the copied `telltale.json` somewhere disposable if you do not want that.

## Code of conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.
