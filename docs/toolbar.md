# FruitLib toolbar slots (superseded)

The release build of FRUKT replaced the fixed toolbar with an inventory, so FruitLib no
longer adds toolbar slots. Custom items now go into the inventory. See
**[inventory.md](inventory.md)**.

`FruitToolbar.Register` still compiles and works. It puts the item on the inventory's
Tools shelf, where the player can send it to any slot. The details are under
[Migrating from FruitToolbar](inventory.md#migrating-from-fruittoolbar).

The slot-injection implementation this page used to describe was removed in FruitLib
4.0.0. It is in git history as `FruitToolbarNative.cs` if you ever need it for a pre-release
build.
