# Developer Notes — Ostranauts Source Bugs Catalog

One file per source file in the shipped `Assembly-CSharp.dll`. Each note documents the most significant real bug(s) found in that file (decompilation artifacts excluded), the root cause, and the recommended upstream fix.

Notes are organized by source filename. Files from subfolders are prefixed with the namespace path.

## Index

### Core simulation
- [CrewSim.md](CrewSim.md) — per-frame raycast/LINQ/GetComponent allocations in `Update`/`GetMouseOverCO`
- [CondOwner.md](CondOwner.md) — `GetJobActions` double `GetComponent`; `Cleanup` `new List<string>(Keys)` per call
- [StarSystem.md](StarSystem.md) — `Update` allocates `dictShips.Values.ToList()` every frame
- [WorkManager.md](WorkManager.md) — `Task2.UpdateTint` allocates `MaterialPropertyBlock` + `GetComponent` per task per frame
- [CollisionManager.md](CollisionManager.md) — `.ToList()` + O(n) `Contains`/`IndexOf` in collision hot path; `Debug.Log` spam
- [Ship.md](Ship.md) — `GetPeople` allocates a new `List<CondOwner>` on every call from per-tick hot paths
- [BodyOrbit.md](BodyOrbit.md) — `UpdateTime` recomputes parent chain every tick (cached depth would help)

### Data / save / load
- [LoadManager.md](LoadManager.md) — synchronous `File.ReadAllBytes` on main thread; `SaveScreenShot` leaks `targetTexture`
- [DataHandler.md](DataHandler.md) — `lock (new object())` never serializes; `GetCondTrigger` clones per call
- [Interaction.md](Interaction.md) — `GetReply` heavy per-reply GC churn (lists + split arrays + Interaction instances)
- [CondTrigger.md](CondTrigger.md) — `nRecursion` never decremented, permanently breaks `RequiresHumans`
- [BeatManager.md](BeatManager.md) — `GenerateTension` re-runs every frame when no event fires
- [AIShipManager.md](AIShipManager.md) — `RunAIQueue` allocates two `List<AIShip>` per frame

### GUI / input
- [GUIPDA.md](GUIPDA.md) — `GetKnownSocialContacts` O(n²) via `Insert(0, ...)`; `Resources.Load` per open
- [CanvasManager.md](CanvasManager.md) — NRE inside null-guard branch (`component.name` after null check)
- [AudioManager.md](AudioManager.md) — `bLoadingMusic` stuck true on exception (no `try/finally`)
- [GUIInventoryItem.md](GUIInventoryItem.md) — per-frame dict lookups in `Update`; `Debug.Log` on every destroy
- [GUIInventory.md](GUIInventory.md) — per-frame `+=` tooltip string building; `Debug.Log` on selection
- [GUIPaperDollManager.md](GUIPaperDollManager.md) — per-frame `List<PixelPos>` + nested `GetComponent`; per-click `PointerEventData` alloc

### Pathing
- [Pathfinder.md](Pathfinder.md) — per-frame `new List<CondOwner>`; `GetPath` allocs List+HashSet per search
- [JumpPointSearch.md](JumpPointSearch.md) — allocates `bool?[,]` 2D array (80KB+) per path search
- [AStarSearch.md](AStarSearch.md) — hardcoded magic `3333` iteration cap; `Debug.Log` on >1511 tiles

### Core / utility
- [InteractionObjectTracker.md](InteractionObjectTracker.md) — `Keys.ToArray()` + LINQ `ToDictionary` rebuild on trim
- [LogHandler.md](LogHandler.md) — `string.Split` on every log message (O(n) in log size)
- [Blackboard.md](Blackboard.md) — `Split`+LINQ `LastOrDefault` per deserialized entry
- [ProgressBar.md](ProgressBar.md) — `Color.Lerp` divisor hardcoded `100f` (logic bug on non-100 max)
- [GridLayout.md](GridLayout.md) — O(n²) `FindNearestUnoccupiedTile`; O(n) grid scan per CO lookup
- [MonoSingleton.md](MonoSingleton.md) — `Awake` continues after `Destroy` (no `return`); `FindObjectOfType` per access
- [ElectricalSignal.md](ElectricalSignal.md) — `int.Parse`/`bool.Parse` throw on malformed save (no try-catch)
- [GUIControls.md](GUIControls.md) — duplicate condition makes middle-page branch dead code
- [InputManager.md](InputManager.md) — `+=` string building in `GetGlyphString`; `SteamInput` array alloc per `GetControllerType`
- [CommandUINavigate.md](CommandUINavigate.md) — iterates all `Selectable`s with `GetComponent` per nav input
- [GUIQuickBar.md](GUIQuickBar.md) — LINQ `ToList` + O(n²) `Remove`/`Insert` reordering
- [GUIInventoryWindow.md](GUIInventoryWindow.md) — `Physics.OverlapBox` + 25 `List<CondOwner>` allocs per frame per ground window
- [ContextMenuPool.md](ContextMenuPool.md) — fixed pool of 50, no growth, returns null + `Debug.Log` spam
