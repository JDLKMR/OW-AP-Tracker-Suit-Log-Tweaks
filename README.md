# Outer Wilds AP Tracker/Suit Log Tweaks
An Outer Wilds mod meant to be installed alongside Ixrec's Outer Wilds Archipelago Randomizer and/or Damián Garro's Suit Log. It makes a few small quality-of-life changes to how the AP Tracker and the Suit Log handle their lists.

If one or both of the two mods that this mod affects is uninstalled, then it just ignores those features.

## Archipelago Randomizer Changes
- The AP Tracker remembers your place when backing out of an entry (applies to both the Ship Log and Suit Log). Backing out of a category returns you to that category instead of the top of the list.

## Suit Log Changes
- Lists such as the AP Tracker remember your place after exiting them and then opening the Suit Log again.
- When using a gamepad, the D-Pad Up input that opens the Suit Log no longer immediately inputs "up" into the list, which would take you to the bottom.

## Building
Requires .NET SDK:

```
dotnet build -c Release
```

## AI Disclosure
All programming is done using Claude. Testing, documentation, and any other writing is all done by me.
