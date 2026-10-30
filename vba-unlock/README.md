# VBA Unlock

Clears the VBA project password from Excel macro workbooks, so the project opens in the VBA editor without a password prompt. Excel is not required, and the original file is left untouched: a copy named `NAME_unlocked.EXT` is written next to each input. Only the password is cleared; the module code is unchanged.

## Use

- Drag one or more workbooks (or folders) onto `Unlock.bat`, or
- Double-click `Unlock.bat` and choose the workbooks in the dialog.

Handles `.xlsm`, `.xlam`, and `.xls`. Folders are searched for these files.

## Note

Use only on files you are authorized to modify.
