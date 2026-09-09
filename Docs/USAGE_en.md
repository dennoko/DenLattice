# Dennoko Lattice User Manual

A non-destructive 3D lattice deformation (Free-Form Deformation) tool designed for VRChat avatar modification.
Easily adjust clothing fit and avatar proportions smoothly by simply manipulating control points on a 3D cage. Original mesh asset files are never modified.

## Requirements

- Unity 2022.3.22f1 or later
- VRChat SDK
- **NDMF (nadena.dev.ndmf)** — Required

---

## Quick Start

### 1. Add Component
Right-click the target mesh (`SkinnedMeshRenderer` / `MeshRenderer`) in the Hierarchy and select `dennokoworks > Dennoko Lattice`.

### 2. Enter Edit Mode
Click the **"Start Editing"** button in the Inspector. (*Automatically enters edit mode when added via the right-click context menu).

### 3. Edit
Click control points in the Scene View (or drag to marquee select) and move them using the transform handle.

### 4. Finish Editing
Press the `Esc` key, or click **"Finish Editing"** in the Inspector or on-screen overlay.

That's it!

---

## Detailed Guide

### 1. Attaching the Component

In the Hierarchy, right-click the GameObject with the target mesh and select `dennokoworks > Dennoko Lattice` (or add it via `Add Component` in the Inspector).

- **When added via right-click on an object with a Renderer:**
  The mesh is automatically registered as an edit target, the lattice box automatically fits to the mesh bounds, and **Edit Mode begins immediately**.
- **When added with multiple objects selected:**
  **All selected meshes are registered as deform targets**, and the lattice box fits around all of them. Only one component is added, on the object you right-clicked (or, when that cannot be determined, the one selected last).
- **When added to an object without a Renderer:**
  Only the component is added. Specify the desired Renderer in the **"Add Target"** field in the Inspector.

The object that received the component is briefly highlighted in the Hierarchy.

To add more targets later, **drag objects that have a mesh into the empty "Add Target" field** at the bottom of the "Targets" list in the Inspector. You can drop several at once.
Registering both clothing and the body mesh allows you to deform them together within the same lattice space without clipping.

### 2. Editing in the Scene View

Click the **"Start Editing"** button in the Inspector to enter Edit Mode.

| Action | Result |
| --- | --- |
| Click a control point | Selects a single control point (highlighted with an orange double ring) |
| **Shift + Click control point** | Toggle add/remove from current selection |
| **Drag on empty area** | **Marquee (box) select multiple control points** (hold Shift to add to selection) |
| Drag transform handle | Moves selected control points and smoothly deforms the mesh vertices inside the box |
| `Esc` | Deselects control points. Press again with no selection to exit Edit Mode |
| Select another object | Exits Edit Mode |

Use the on-screen overlay in the bottom-right of the Scene View to quickly adjust resolution, mirror symmetry, and box placement on the fly.

### Undo & Redo (Ctrl+Z / Ctrl+Y)

**Each handle drag operation counts as a single Undo step.** Pressing `Ctrl+Z` instantly undoes the latest drag (`Ctrl+Y` to Redo).
Edits are recorded as lightweight binary snapshots, ensuring lightning-fast Undo performance without freezing even on complex avatar prefab instances.

### 3. Upload Directly

No export or manual baking is required. Edits are stored non-destructively on the component and automatically applied during the avatar build and upload process via NDMF.

---

## On-Screen Overlay (Scene View)

The panel in the bottom-right of the Scene View (draggable by its header) provides direct access to frequent editing settings.

### Resolution (Grid Divisions)
Adjust grid resolution along the X, Y, and Z axes using `[-]` `[Number]` `[+]`.
**Changing resolution preserves all existing mesh deformations.** (You can freely start with a coarse grid for rough shaping and refine later with a denser grid).

### Mirror Symmetry
Toggle symmetry along axes using the mutually exclusive `[ X ]` `[ Y ]` `[ Z ]` buttons:
- Clicking an axis enables mirror deformation along that axis (highlighted in green).
- Clicking the active axis again turns mirror symmetry OFF.
- For bilateral left-right symmetry, ensure the box is centered on the avatar using the "Center" button.

### Edit Box Mode
Toggle between editing control points and transforming the lattice bounding box itself:
- **Move / Rotate / Scale**: Adjust box dimensions directly with scene gizmos.
- **Fit**: Automatically sizes and positions the box around all target meshes with safety margins.
- **Center**: Centers the box on the avatar's symmetry plane ($X=0$) and resets rotation.

### Reset Cage
Resets all control points back to their initial grid positions while **preserving the deformed mesh shape**. The box automatically refits to enclose the deformed mesh.

---

## Interpolation Modes

Configure how displacement spreads from control points in the Inspector:

| Mode | Characteristics |
| --- | --- |
| **Trilinear** | Moves only the neighboring cells around control points. Best for sharp, local edits. |
| **B-Spline** | Provides the smoothest overall curve. Ideal for organic, natural surfaces (does not pass directly through control points). |
| **Catmull-Rom** | Passes directly through control points while maintaining smooth cubic continuity. Great balance of precision and smoothness. |

---

## Baking

Export the deformed mesh directly to a new asset file in your project.

- Saved in the **same folder as the source mesh**.
- Named with the **`_lattice`** suffix appended to the original mesh name.
- Numbered sequentially if a file with the same name already exists.

### Add as BlendShape
When checked, baking outputs a mesh preserving the original base geometry while **adding the deformation as a new BlendShape (shape key)**.
Specify the shape key name in the "BlendShape Name" field in the Inspector (defaults to source mesh name + `_lattice` when empty).

> **Note:** Baking exports asset files and does not replace Renderers in the active scene. To use the baked mesh, assign it manually to the Renderer's Mesh slot. Baking is not required for standard VRChat avatar uploads.

---

## Specifications & Limitations

### Supported Features
- Intuitive and smooth 3D Free-Form Deformation (FFD)
- Multi-mesh simultaneous editing (Body, Clothes, Accessories)
- Non-destructive resolution adjustment (X, Y, Z)
- Multi-axis mirror editing (X, Y, Z)
- Marquee rectangle selection for batch control point manipulation
- Real-time NDMF preview synchronization
- Mesh asset export & BlendShape baking

### Out of Scope (By Design)
- Adding/removing vertices or changing mesh topology
- Direct editing of UVs, normals, or bone weights

### About Normals & Shading
**Normals and tangents are not automatically recomputed.** While extreme deformations may not reflect lighting changes, recomputing normals alters original shading and mesh seams, which is why this behavior is intentional.

### Vertex Index Dependency
Like standard BlendShapes, this tool stores vertex displacement deltas mapped to **original mesh vertex indices**.
- Re-exporting/re-importing FBX files with altered vertex orders may cause misalignment.
- If the mesh vertex count changes, deformation is **safely skipped automatically** to prevent model distortion, and a warning is displayed in the Inspector.

### Compatibility with Other Tools
Processed in NDMF's `Transforming` phase before optimization tools like Avatar Optimizer (AAO), ensuring seamless and stable coexistence.

---

## Troubleshooting

### "NDMF Preview Not Found"
The NDMF preview is either disabled or initializing. Check the Unity toolbar or top menu to ensure NDMF preview is enabled.

### "Vertex count does not match edit state"
The source mesh asset was replaced or re-imported with a different vertex count.
Deformation is safely paused. Use "Clear All Deformation" in the Inspector to reset and re-edit.

### Edits Are Not Visible
- Verify that the `DenLattice` component and GameObject are active.
- Ensure target Renderers are assigned.
- Confirm NDMF preview is active in the Scene View.
