# Alcyone user guide

[Project overview](../README.md) · [简体中文](guide.zh-CN.md)

## Recognition libraries

Open **管理 / 导入** (Manage / Import) in the recognition section. Create or select a dictionary or multi-point color library. The embedded editor provides capture, labeling, training, and testing without launching a separate tool.

Save to the recognition library to update the current item while keeping its ID and existing condition references. Save as a new item or dictionary to append a separate entry. Libraries and conditions travel with workflow configuration imports and exports.

| Item | Supported input | Behavior |
| :-- | :-- | :-- |
| Dictionary | Tool-exported TXT / JSON or FTF1, up to 64 MB | Import a dictionary, then select OCR or fixed-text search. |
| Multi-point color | Full copied tool invocation | Parse weights, search region, color tolerance, and similarity as data; imported code is not executed. |
| Multi-point color | FCF2 / FCF3 feature string | Import features while keeping the current search parameters. |

The table exposes names, glyph matrices or color descriptions, feature strings, and search regions. Dictionary templates share one search region; color entries have independent parameters. Selecting a row opens its settings. Invalid input reports an error and preserves the previous valid value.

Use the region-selection control to draw a search rectangle on the desktop. The result saves automatically; Escape cancels selection. Reopening an existing item restores its training data. Older color features without capture positions need an initial recapture. Capture regions and search regions are independent.

### Training and testing

- **Dictionaries:** switch dictionaries, import / export TXT, edit glyph pixels, weights and colors, run OCR or matching tests, and export calling code or a C# class. Save each dictionary to the library separately.
- **Colors:** use the live canvas, magnifier, drag and keyboard adjustments, timed training, continuous tests, and search-region controls.
- **Errors:** malformed dictionaries or capture failures are execution errors. A valid search with no match is a recognition failure, not an execution error.

## States and transitions

Each state belongs to a region and can run actions on entry, during its loop, and on exit. Connect output ports to input ports to define transitions. Each region has its own current state.

Choose a global variable, a color item, or a dictionary in an output condition. Variable conditions compare values. Recognition conditions can inspect success / failure, similarity, X, Y, and text together; every populated field must match. Empty numeric and text fields are ignored. Similarity and coordinates have their own comparison operators.

| Mode | Meaning |
| :-- | :-- |
| ALL | Every condition must match. |
| ANY | At least one condition must match. |
| ALWAYS | Unconditional transition. |

Outputs are evaluated from top to bottom. If several match, only the first is taken. Each cycle checks recognition items referenced by the current states' outputs.

For a dictionary condition, choose **OCR result** to inspect recognized text across the search region, or **fixed-text search** to locate a required string. One dictionary can independently support OCR and searches for different strings. The library panel's mode is used for testing.

Global variables support numbers, booleans, text, and JSON. Recognition results remain separate from ordinary variables. Legacy color variables are migrated into recognition items and success / failure conditions.

## AI assistance

Open **AI 开发** (AI Development), choose workflow + scripts, scripts only, or workflow only, and describe the behavior you want. The assistant receives current workflow context, recognition metadata, and the available action catalog. Inspect the proposed changes and choose **应用变更** (Apply Changes) to apply them.

Example request:

> Use an existing recognition item to wait for a target. Click its coordinates once, wait for it to disappear, then return to the waiting state. Add logs and keep the current recognition library.

See the [AI setup instructions](../README.md#configure-ai-optional). Train real recognition features before asking the assistant to reference them.

## Scripting API

Scripts are `.csx` files inheriting `StateScript`. Mark public methods with `[StateAction]` and configurable parameters with `[StateParameter]`. The runtime watches the executable's `HotScripts` directory, compiles changes, and retains the last working version when compilation fails. Source copies live in `Alcyone/HotScripts/`.

| API | Purpose |
| :-- | :-- |
| `Vars.Get<T>(name, defaultValue)` / `Vars.Set(name, value)` | Read or write shared variables. |
| `Api.Recognition.Last(name)` | Read the most recent result; it may be null. |
| `await Api.Recognition.FindAsync(name, seconds: 3)` | Search using a recognition item's parameters. |
| `hit.Found`, `X`, `Y`, `Similarity`, `Text` | Inspect status, coordinates, score, and text. |
| `Api.Input.MoveTo(x, y)` / `Click(x, y, "left")` | Move or click the mouse. |
| `await Api.Input.DoubleClickAsync(x, y)` | Double-click. |
| `Api.Input.Press("ENTER")` / `Hotkey("CTRL", "A")` | Send a key or key combination. |
| `Api.Input.TypeText(text)` | Enter Unicode text. |
| `Api.Input.KeyDown(key)` / `KeyUp(key)` | Hold or release a key. |
| `Api.Input.MouseDown()` / `MouseUp()` / `Scroll(120)` | Hold, release, or scroll the mouse. |
| `await Api.Input.DragAsync(x1, y1, x2, y2)` | Drag between coordinates. |
| `await Api.Delay(milliseconds)` / `Api.CancellationToken` | Use cancellable waits and long operations. |
| `Log(message)` | Write to the execution log. |

`Api.Color` remains a compatibility alias for `Api.Recognition`. Check `Found` before clicking. Coordinates are physical pixels in the virtual desktop; secondary monitors can have negative coordinates.

Stopping or resetting cancels built-in waits and searches and releases keys and mouse buttons held by scripts. Custom long-running loops must observe `Api.CancellationToken`.

## Runtime and files

`Execution/MachineRuntimeService` manages instances by configuration. Each `MachineSession` owns its states, recognition loop, hot-script host, and scheduling thread. Razor pages submit actions and subscribe to snapshots.

- Refreshing or disconnecting the page does not destroy an instance.
- Switching configurations keeps existing instances alive.
- Stopping is explicit; deleting a configuration releases its instance.
- Exiting the application stops the service. Restarting does not automatically resume execution.
- Runtime snapshots do not trigger workflow serialization or saving.

Editable configuration and scripts live in `StateMachineConfigs/` and `HotScripts/` beside the executable. These directory names are independent of the Alcyone product name.

## Building and verification

See [Build & distribute](../README.md#build--distribute) for commands and runtime requirements. Source builds require the sibling `FastColorFinder` directory. The project links its capture, selection, training, and recognition code; published builds do not need these sources nearby.

`--browser-host` prints a loopback URL for browser development. `--check-host` verifies script compilation, the main page, and embedded training resources. This repository does not include standalone TrainingChecks or RecognitionImportChecks test projects.

If a directory move leaves stale scoped CSS in a local build, rebuild before publishing:

```powershell
dotnet build Alcyone.slnx -c Release -t:Rebuild
```
