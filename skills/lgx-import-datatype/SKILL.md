---
name: lgx-import-datatype
description: Change an existing user-defined data type (UDT) - list types, preview the member delta against the open L5X project, then write the new definition as an L5X fragment for the engineer to import in Logix Designer. Existing types only; new types are created in Logix Designer.
---

# Change a data type, safely, as an L5X fragment

This build reads and writes **L5X only**. `logix_import_datatype` never edits an `.ACD` and never
touches the controller: its real run writes an **L5X fragment** (`saveAs` ending in `.L5X`) that the
engineer imports in Logix Designer (Logic > Import Component, or Tools > Import). An `.ACD` `saveAs`
is refused with `SdkRequired`; nothing is written.

**Existing types only.** The type must already exist in the open project. If it does not, a real run
is refused with `NewDatatypeUnsupported` (nothing is written), and a dry run shows `willCreate: true`
plus a `warnings` entry. Create new types in Logix Designer, export the project to L5X, then use this
skill for later changes.

1. `logix_open_project(path)` with the project's `.L5X` export. The delta is computed against this
   open project, so open it first.
2. `logix_list_datatypes(nameContains=...)` for the current layout.
3. **Dry run (default):** `logix_import_datatype(acdPath, datatypeName, l5xPath | members, saveAs)`.
   Pass the project's `.L5X` path as `acdPath`, and a new `.L5X` path as `saveAs`. The delta
   (added / removed / retyped / renamed / reordered) is shown against the open project. Show it to the
   user; call out removals and retypes — tag data in those members does not survive.
4. `dependentTags` lists tags of that type; they keep their names but their data follows the new layout.
5. **Real run:** repeat with `dryRun=false, confirm=true`. `saveAs` must differ from `acdPath` and must
   not already exist (`TargetExists`) unless you pass `overwrite=true`. The input file is never
   modified. If a real run is called without `confirm=true`, it returns the delta plus `refused`
   and writes nothing.
6. The response has `verified: null` and a note that only an L5X fragment was written: nothing has
   been imported into any project yet. Say so.
7. Importing the fragment and applying it to a controller (a **download**, which stops and reloads the
   controller) is the engineer's decision and step, not yours.

Malformed or oversized L5X input is refused (`InvalidL5x`, `InputTooLarge`) before anything is parsed
or written.
