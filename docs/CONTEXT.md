# Context

## Glossary

### Sledding UI Adapter

The mod's UI layer that discovers, clones, styles, and modifies existing Sledding Game UI objects for mod-owned screens and controls. It should preserve the game's native visual language by reusing built-in UI templates instead of introducing a separate generic UI style.

### UILib

The current static facade for UI helper methods. During the rewrite, it is a compatibility layer over the Sledding UI Adapter and should not be treated as the canonical long-term domain name.

### Native UI Template

An existing Sledding Game UI object or component used as the source for cloned mod UI controls, including labels, buttons, inputs, toggles, sliders, panels, backgrounds, shadows, and text styles.

### Staged Setting

A setting edited in the settings window but not committed to preferences or live game behavior until the user presses Apply.

### Settings Draft

The settings window's temporary copy of all displayed settings. It is created from committed preferences when the settings window opens, mutated by controls, checked for unapplied changes, and committed only by Apply or Apply/Close.

### Sledding UI Path

A known Sledding Game object name or transform path used to find native UI objects. These paths are centralized so UI changes in the game can be handled in one place.
