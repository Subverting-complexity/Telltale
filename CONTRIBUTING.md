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

- **C#**: follow the conventions already in the codebase. The projects target .NET 10 with nullable reference types enabled.
- **TypeScript/React**: follow the existing patterns in `frontend/src/`. Use TypeScript strict mode.
- Keep changes focused. If you spot an unrelated issue while working, open a separate PR for it.

### Testing

All pull requests should pass the existing tests and include tests for new behaviour where practical.

**Backend tests** (from the repo root):

```bash
dotnet test Telltale.slnx
```

**Frontend tests**:

```bash
cd frontend
npm test
```

Note that the collector tests exercise Windows-specific P/Invoke code and require a Windows machine to run. The frontend tests run on any platform.

## Code of conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you are expected to uphold this code.
