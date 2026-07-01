# AssetPipelineTrace

Debugging tool that allows you to trace changes in assets.

### `ap-trace [assetName]+`

Supports data (e.g. `ap-trace Data/Objects`), maps (`ap-trace Maps/Forest`), and textures (`ap-trace LooseSprites/Cursors`).

This starts a trace of specified asset(s). Once you see the DEACTIVATED message you can check the results.

In order to complete the trace the game must reload the target asset.

### `ap-trace-map`

This command traces the current location's map, and then begin logging what mod added the tile directly under your mouse.
Run this again or move to another location to stop.
