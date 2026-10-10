# Script editor assets

The C# workbench uses Monaco Editor, bundled locally for offline desktop use.

Rebuild after changing `workbench.js`:

```powershell
cd Tools/ScriptEditor
npm ci
npm run build
```

Commit the generated `Alcyone/wwwroot/editor` assets along with the source and lockfile.
Normal .NET builds do not require Node or network access. Monaco's MIT license is included
in the generated directory. C# coloring and application API snippets run locally;
Roslyn compilation diagnostics come from the existing hot-reload host, not an LSP server.
