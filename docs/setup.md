# Device setup

Virtual output uses vJoy 2.2.2 x64. A dedicated pedal device requires only
X, Y and Z axes, no buttons/POVs and no force feedback. Game bindings are
X=throttle, Y=brake, Z=clutch. Remove duplicate physical-pedal bindings in the game.

Automatic selection uses the lowest free compatible ID from 1–16 and stores
it per user in `virtual-device.txt`. Reconnects retain that ID even if busy,
so game bindings remain stable. Close other feeders to release a busy device.

`VJoySetup` creates an unused ID or recreates a missing remembered ID. It
preserves existing descriptors and never passes the upstream overwrite flag.
After creation it inspects driver state again: helper exit zero alone does
not establish readiness.

SherpaAxis does not edit game configurations or hide physical devices.
Driver installation and physical/simulator acceptance require separate testing.
