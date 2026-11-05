# Spider

Spider is a work-in-progress 2D/2.5D action game made in Unity, where you play as a spider. The legs are driven by procedural inverse kinematics (IK) rather than canned animations. The spider spins webs and climbs or rappels on them, and the web strands are simulated with Verlet physics. The world is procedurally generated terrain that can be dug into and destroyed. Centipede and bird enemies have their own AI.

> TODO (Travis): adjust this description to match how you'd pitch the game.

## Features in the code

- **Procedural spider IK** (`Assets/Scripts/PlayerSpider/`): `SpiderControllerIK`, `SpiderLegIK`, `SpiderIKUtility` and `LegLayerManager` handle leg placement, stepping and body movement. `Experimental/` holds a second iteration of the controller and leg.
- **Webs** (`Assets/Scripts/Web/`): `SpiderWebShooter`, Verlet-simulated strands (`WebVerletStrand`, `WebSegment`), strand rendering (`WebStringRenderer`), and web attachment points (`GlobalAttachmentManager`).
- **Destructible procedural terrain** (`Assets/Scripts/TerrainGen/`, `ProceduralTerrainGenerator`): diggable terrain chunks with a chunk LOD manager. It uses Clipper2 for polygon clipping and triangulation.
- **Enemies and AI** (`Assets/Scripts/AI/`): `SpiderAI`, `SpiderPathfinding`, `CentipedeAI` with `CentipedeIK`/`CentipedeLegIK`, and `BirdController`.
- **Other**: grid camera (`Camera/GridCameraController`), parallax layers (`Parallax/`), save state (`Utility/SaveStateManager`) and game setup (`GameSetup`).

## Built with

- Unity **6000.3.10f1** (Unity 6), Universal Render Pipeline (URP 17.3)
- C#

## Requirements

- Unity Hub with Unity 6000.3.10f1 installed. TODO (Travis): add WebGL Build Support if you want web builds.
- Windows, macOS or Linux editor. TODO (Travis): confirm the platforms you've tested.

## How to open and run

1. Clone the repo: `git clone https://github.com/TravisWoolston/Spider.git`
2. In Unity Hub choose **Add → Add project from disk** and select the cloned folder. Open it with 6000.3.10f1 and let packages resolve.
3. Open `Assets/Scenes/Overworld.unity` and press **Play**. It contains the player spider prefab. TODO (Travis): confirm this is the main scene. `EditorBuildSettings` currently lists only `SampleScene` (disabled).

## Project structure

```
Assets/
  Scripts/
    PlayerSpider/   Spider and centipede IK, controllers (Experimental/ = v2 iteration)
    Web/            Web shooting, Verlet strands, rendering
    TerrainGen/     Destructible terrain and chunk LOD
    AI/             Spider, centipede and bird AI, pathfinding
    Clipper/        Third-party Clipper2 library (Boost license)
    Camera/, Parallax/, Utility/
  Objects/          Prefabs (e.g. PlayerSpiders/)
  Scenes/           Overworld, SampleScene
  Environment/, Shaders/, Settings/, Images/
  SkySeries Freebie/, QS/   Third-party asset packs
Packages/, ProjectSettings/
```

## Third-party credits

- **Clipper2** by Angus Johnson (https://www.angusj.com) in `Assets/Scripts/Clipper/`, under the Boost Software License 1.0. See `Assets/Scripts/Clipper/LICENSE`.
- **SkySeries Freebie** skybox materials and HDRIs in `Assets/SkySeries Freebie/`. TODO (Travis): add the source (Unity Asset Store page) and license, normally the Asset Store EULA.
- **QS / Quadrante Studio** materials and backgrounds in `Assets/QS/` (swg2.com.br). TODO (Travis): add the source and license, normally the Asset Store EULA.
- TODO (Travis): list any other imported assets.

Third-party assets keep their own licenses and are not covered by this project's MIT license.

## Roadmap

- TODO (Travis): gameplay goals and progression
- TODO (Travis): more enemies or creatures
- TODO (Travis): IK, web and terrain improvements
- TODO (Travis): builds and release (e.g. WebGL)

## License

The code in this project is MIT licensed. See [LICENSE](LICENSE). Third-party code and assets are excluded (see above).
