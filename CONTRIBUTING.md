# Contributing

Thanks for taking a look. Waymark is a small project, so keep changes small and easy to read.

## Before you start

- Open an issue first for anything bigger than a bug fix, so we can agree on the idea before you spend time on it.
- Security problems go through [SECURITY.md](SECURITY.md), not a public issue.

## Build

You need Windows 10 or 11, git and the .NET 10 SDK (the installer adds the SDK for you).

```powershell
git clone https://github.com/patilprashant6792-official/waymark-local.git
cd waymark-local
dotnet build Waymark.slnx -c Release
```

The build should finish with 0 errors and 0 warnings. To try your change in a running copy, run `install.cmd`; it builds the working tree and restarts Waymark.

## Tests

There is no automated test project in this repository. Check a change by building it and by using the feature yourself, and say in the pull request what you tried.

## Pull requests

- One idea per pull request, with a short description of what changed and why.
- Follow the style of the code around your change. Prefer plain, readable code over clever code.
- Update the page in `docs/` if your change alters what users see or what a setting does.
- Do not add secrets, personal paths or machine names to code, docs or screenshots.

## License

By contributing you agree that your contribution is released under the [MIT License](LICENSE), like the rest of the project.
