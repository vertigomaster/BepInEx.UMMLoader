# BepInEx.UMMLoader

A BepInEx-compatible loader for [UnityModManager](https://github.com/newman55/unity-mod-manager).

Current version is based on UMM 0.16.1 (with various bespoke fixes for the port) and supports most of its functionalities.  
Parts of the source was rewritten for clarity and integration with BepInEx.

Written for BepInEx 5, which you can currently get as [bleeding edge builds](http://bepisbuilds.dyn.mk/bepinex_be).

*Currently WIP, but functional*

## Known Issue(s):
- Enabled status for active mods will incorrectly display as "Need Restart", but this is a visual-only issue. The mods should still be active. If they are not, save the mod list and restart - that will for sure activate them, even if it continues to display as "Need Restart".
- UMM Mods requiring Harmony 1.x are no longer supported; its not feasible for newer versions of BepInEx like BepInEx 5
