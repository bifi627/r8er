# r8er signaling protocol

Contract-first (per CLAUDE.md). This is the **POC seed** — just enough to
carry a WebRTC handshake between two peers through the signaling relay. Codegen
(C#/Go/TS from one schema) is deferred until the protocol stabilizes; for now
the envelope is hand-mirrored in Go (`agent/cmd/agent/main.go`) and JS
(`dev/throughput.html`).

## Transport

A peer opens a WebSocket to `/signaling/{room}`. The relay forwards every frame
verbatim to the *other* peer in the room (max two peers). It never inspects
payloads. Rendezvous is by shared `room` id, agreed out-of-band (hardcoded
`poc` for the POC).

## Message envelope

One JSON object per WebSocket text frame:

```json
{ "type": "offer" | "answer" | "ice", "sdp": <SessionDescription>, "candidate": <ICECandidateInit> }
```

| `type`   | carries      | direction        |
|----------|--------------|------------------|
| `offer`  | `sdp`        | browser → agent  |
| `answer` | `sdp`        | agent → browser  |
| `ice`    | `candidate`  | both (trickle)   |

- `sdp` is the WebRTC `RTCSessionDescription` (`{type, sdp}`), present only on `offer`/`answer`.
- `candidate` is `RTCIceCandidateInit` (`{candidate, sdpMid, sdpMLineIndex, ...}`), present only on `ice`. Candidates trickle as they are gathered; end-of-candidates (a null candidate) is not signaled — POC lets gathering finish naturally.

## POC roles

The **browser is the offerer** and creates the data channel; the **agent is the
answerer**. The agent must be in the room first (it is the long-lived peer). No
auth, no tenancy — MVP promotes both the relay and this envelope into the
tenant-isolated broker.
