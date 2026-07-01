# AssetPipelineTrace

Debugging tool that allows you to trace changes in assets.

### `ap-trace [assetName]+`

Supports data (e.g. `ap-trace Data/Objects`), maps (`ap-trace Maps/Forest`), and textures (`ap-trace LooseSprites/Cursors`).

This starts a trace of specified asset(s). Once you see the DEACTIVATED message you can check the results.

In order to complete the trace the game must reload the target asset.

### `ap-trace-map`

There's also a command to trace the current location's map `ap-trace-map`.
