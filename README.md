# RFIDPoc

A proof of concept for RFID baggage reconciliation for Kenya Airways flight **KQ-504** (Nairobi → Entebbe), driven by a real **Alien ALR-9900+ EMA** UHF reader.

The app follows each bag through check-in, the sorting tunnel and loading into the aircraft hold. It refuses to let a bag be loaded unless the bag matches the flight. Every hand-off is recorded in an audit trail that can't be edited.

It is built from the specification in [`KQ-Baggage-Tracking-POC-CSharp-Spec.md`](KQ-Baggage-Tracking-POC-CSharp-Spec.md). Parts of the spec that are distributed (API, IBM MQ, PostgreSQL, the handheld app) are simulated inside one Windows desktop app, so the whole flow can be demonstrated with one reader in a boardroom.

## What it shows

- **Real tags, real reads.** The reader streams every tag read to the PC (Alien tag stream over TCP). A decision is made within a second of the first read.
- **Three scan points on one reader:**

  | Antenna | Scan point | What a read does |
  |---|---|---|
  | 0 | Check-in · Desk 14 | Confirms the bag tag against the passenger's BSM (FR-02) |
  | 1, 2 | Sorting tunnel · Belt 04 (left and right) | One de-duplicated scan per bag; **Sorted** + BPM, or a wrong-flight alarm |
  | 3 | Loading · Hold 2 (stands in for the ramp handheld) | Load decision into ULD AK7/AK8: **LOAD**, or a **DO NOT LOAD** alarm |

- **Writing bag tags.** The bag's 10-digit licence plate can be written onto a blank tag at the check-in antenna. A tag that already belongs to another passenger's bag is never overwritten.
- **Load rules and exceptions.** Wrong flight, no authority to load, passenger no-show and offloads (FR-05 to FR-10). A refused load locks the app with a full-screen alarm until the bag is rescanned or a supervisor overrides it.
- **IATA Type B messages.** BSM in; BPM and BUM out, with an outbox that queues while the "MQ link" is down.
- **Live map.** A floor plan of the terminal, baggage hall and apron, showing every bag and which antennas are reading right now.
- **Reports.** Read rate per scan point and antenna, exceptions and time to resolve, exportable as Markdown or CSV.
- **Simulator.** Optionally generates the full 700-bag flight with virtual bags and injected faults.

## Projects

| Project | What it is |
|---|---|
| `KQ.Brs.Poc` | The WinUI 3 app: dashboard, live map, live reads, loading, exceptions, Type B messages, reports, settings |
| `KQ.Brs.Core` | Domain model, single-threaded reconciliation engine, load rules, Type B parser and builder, SQLite persistence, simulator, reports |
| `KQ.Rfid.Alien` | Alien reader protocol without the vendor SDK: commands, tag stream listener, tag writing |
| `KQ.Brs.Tests` | xUnit tests for the engine, de-duplication, Type B messages, persistence, tag writing and the reader library |
| `RFIDPoc` | WPF diagnostics tool for the reader: discovery, configuration, Notify and tag stream reading. It uses Alien's .NET SDK in `lib/AlienRFID2` |

## Requirements

- Windows 10 or 11 (x64)
- .NET 10 SDK, and Visual Studio 2022 or later with the Windows App SDK (WinUI) workload
- An Alien ALR-9900+ reader on the same network, with up to 4 antennas. The app also runs without one, using the simulator.

## Getting started

```powershell
dotnet build RFIDPoc.slnx
dotnet test KQ.Brs.Tests
```

Then run `KQ.Brs.Poc` from Visual Studio.

1. In **Settings**, enter the reader's IP address and login, then click **Set POC defaults** for the antennas.
2. On the first run, allow `KQ.Brs.Poc` through Windows Firewall (private networks). The reader connects to the PC on port 4000 to stream reads.
3. Only one app can stream from the reader at a time, so close `RFIDPoc` first.

Data and settings are stored in `%LocalAppData%\KQ.Brs.Poc` (`brs.db` and `settings.json`). The reader password is encrypted with Windows DPAPI.

## Demo

- [`KQ.Brs.Poc/DEMO.md`](KQ.Brs.Poc/DEMO.md): the step-by-step demo, from adding a passenger and writing their tag to loading, misroutes, no-shows and reports.
- [`KQ.Brs.Poc/Setup/KQ-504-Boardroom-Floor-Plan.pdf`](KQ.Brs.Poc/Setup/KQ-504-Boardroom-Floor-Plan.pdf): where to put the antennas in the room.

## Limits of the POC

- One flight, one reader, and a single desktop app instead of the spec's API, MQ, PostgreSQL, Blazor dashboard and MAUI handheld.
- Licence plates are written to tags in a simple POC format (`BA60` + plate + zeros), not yet IATA RP 1740c.
- Type B messages follow RP 1745 but have only been checked against the app's own samples, not real KQ or SITA traffic.
