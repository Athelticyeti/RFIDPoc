# KQ-504 baggage reconciliation POC: demo guide

This POC compresses the spec (`KQ-Baggage-Tracking-POC-CSharp-Spec.md`) into one Windows app. The real Alien ALR-9900+ and its four antennas stand in for the spec's three scan points.

| Antenna | Location | What a read does |
|---|---|---|
| 0 | **Check-in · Desk 14** (take-on) | Links the tag to the next passenger waiting for one (as if their bag tag was just printed). A known tag gets a desk check. |
| 1 | **Sorting tunnel · Belt 04 (left)** | Bag → **Sorted** + BPM. A bag for another flight → **WrongFlight** alarm. |
| 2 | **Sorting tunnel · Belt 04 (right)** | Same as antenna 1: the two antennas face each other across the "belt". |
| 3 | **Loading · Hold 2** (handheld) | Load decision (FR-05) into the selected ULD → **Loaded** + BPM, or **DO NOT LOAD** alarm. |

You can change the mapping in **Settings → Antenna → scan point**.

The flight starts **empty**. You add each passenger yourself and check in their bag with a real tag. To simulate a full 700-bag flight instead, switch on **Settings → Generate 700 demo bags** and reset the demo.

## Before the demo

1. **Close RFIDPoc.** Only one app can stream from the reader at a time.
2. **Clear the bench.** Keep spare tags in a closed metal tin, away from all antennas. Hold one tag at a time near the antenna you mean.
3. **Firewall.** On the first start, Windows asks whether `KQ.Brs.Poc` may accept connections. Choose **Allow** on private networks. The reader *connects to this PC* on port 4000 to stream reads. If you dismissed the prompt, open *Windows Defender Firewall → Allow an app* and tick KQ.Brs.Poc.
4. **Start the app.** It connects to `192.168.0.161` and starts streaming; the reader pill at the bottom left turns green. **Live reads** shows each antenna's reads per second.
5. **Start clean:** **Settings → Clean database** (tick "I understand", then *Delete everything*). Licence plates keep counting up across cleans, so tags written earlier never match a new passenger.
6. **Antennas:** **Settings → Antenna → Set POC defaults** sets the calibrated minimum signal levels and arms the loading antenna.

## Walkthrough

1. **Add a passenger.** On the Dashboard, click **Add passenger** and enter the guest's surname, initial, class and number of bags. The app sends their BSM (see **Type B messages → Inbox**), and the bag shows as **EXPECTED**.
2. **Write the bag tag.** Straight after *Add passenger*, the app asks you to write each bag's tag. Hold **one** tag still at **antenna 0** (other tags away) and click **Write tag**. The licence plate is written into the chip and checked by reading it back. That's the POC's "printing the bag tag". If more than one tag is at the antenna, it refuses and asks you to remove the others. **Skip** leaves the bag to be linked to the next plain tag instead. You can also write a tag later from the bag's details: **Write plate to tag**. Once the bag's tag carries its plate, the button becomes **Replace bag tag (write plate to a new tag)**: the "damaged tag, print a new one" case.
3. **Check in (take-on).** Hold the tag at **antenna 0**. The app reads the plate from the chip, confirms it against the BSM (FR-02), and the bag turns **CheckedIn**. A plain (unwritten) tag is linked to the next passenger waiting for one instead. A tag held at check-in with nobody waiting shows *"Tag not linked: add the passenger first"*.
4. **Sorting tunnel.** Carry the tag between **antennas 1 and 2**. The bag turns **Sorted** and a BPM appears in the **Outbox**.
5. **Loading.** Open **Loading · Hold 2**, pick **ULD AK8** and switch **Loading antenna (3) armed** on. Hold the tag at **antenna 3**. The page shows a green **LOAD → AK8** and the bag is **Loaded**. Click the bag on the dashboard to see its full audit trail.
6. **Misroute (spec 7.2).** First switch on **Settings → Demo → Show misroute demo** and save (it is off by default). Add a second passenger and check in their bag. **Move the tag away from antenna 0**, then on the dashboard click the row and choose **Flag tag as KQ-412 (misroute demo)**.
   - A KQ-412 BSM is sent for a **new passenger** with a generated name (e.g. P. Omondi), and the tag moves to that Dar es Salaam bag. Your KQ-504 passenger goes back to **Expected**. On the Live map, their dot slides back to *Awaiting check-in* and a red KQ-412 dot appears under *Checked in*.
   - Keep the tag off antenna 0 while flagging: otherwise the desk reads it again as a wrong-flight bag, which muddles the story.
   - Carry it through the **sorting tunnel**: the Live map shows *"✗ Wrong flight: belongs to KQ412"* by the tunnel, the dot slides into *Problems in the hall*, and the full-screen **DO NOT LOAD** alarm sounds and the app locks.
   - Click **Ready to rescan**, wait a moment, then present the tag at **antenna 3**. The alarm clears, the dot moves to *Refused at loading*, and the audit trail records *"removed and re-routed to KQ412"*.
   - Alternatively, use **Supervisor override** (PIN `1234`, a reason is required).
   - To reset, open the KQ-412 bag and choose **Undo KQ-412 flag**: the tag returns to the KQ-504 passenger.
7. **Ticket problem (FR-05).** Check that **Settings → Authority to load is Y (ticket valid)** is on. The check runs only at loading: check-in and the tunnel let the bag through, and the ramp stops it.
   - Click **Add passenger** with **Ticket valid** switched off. In **Type B messages → Inbox**, the BSM's `.S/` line starts with `N`: the DCS saying "don't load".
   - Write the tag and check it in at **antenna 0**. The bag turns **CheckedIn** with a **Held** badge (amber on the Live map).
   - Carry it through the **sorting tunnel**. It is sorted as normal and a BPM is sent.
   - With the loading antenna armed, present it at **antenna 3**. The full-screen **DO NOT LOAD – No authority to load (ticket not valid)** alarm sounds and the app locks.
   - Clear it with **Supervisor override** (PIN `1234`, a reason is required). The bag's audit trail records the refused load and the reason.
   - Alternatively, show an already-loaded bag losing its authority: load a valid passenger's bag, then on **Type B messages** apply a **CHG** for that plate with `.S/N/...`. The alarm fires, the bag must come off, and it is listed under bags to offload in the **Pre-pushback check**.
8. **Passenger no-show (FR-10).** On **Type B messages**, click **Example: no-show CHG**, check the `.N/` line holds the loaded bag's plate, then **Apply**. The loaded bag must come off: the alarm fires. Rescan at antenna 3 to confirm the offload (a BUM is sent).
9. **MQ outage.** Switch **IBM MQ link** off, scan a tag (the message shows as queued), then switch it back on. The queued messages are sent (at-least-once delivery).
10. **Before pushback (FR-10).** On the Dashboard, click **Pre-pushback check**. The summary reads e.g. *"1 of 1 bag(s) loaded · 0 checked in but not loaded · 0 to offload · 0 never checked in"*.
    - **Clear:** with every checked-in bag loaded and nothing to offload, it shows a green **Clear for pushback**. Bags that were never checked in are counted but don't block pushback.
    - **Bag left behind:** add a passenger, write and check in their tag at **antenna 0** (and sort it if you like), but don't load it. Run the check again: it turns amber, **Not clear for pushback**, and lists the bag under **Checked in, not loaded**.
    - Click **Raise NotLoaded exceptions**. A toast confirms how many were raised, and each bag gets a **NotLoaded** exception.
    - **Bag to offload:** a loaded bag whose passenger isn't flying (step 8) or whose ticket is no longer valid (step 7, CHG with `.S/N/...`) is listed under **Offload required** until it is rescanned off at antenna 3.
11. **Evaluation.** Open **Reports**: read rate per scan point and antenna, exceptions and time to resolve. Use **Export** to save Markdown or CSV.
    - **Turnaround (spec 9.1).** Under **Turnaround vs baseline flight**, enter a comparable flight loaded without RFID (bags loaded, minutes from the first bag into the hold to the last) and click **Save baseline**. The report compares the time per bag with the POC's real-tag loads (at least two), and says whether turnaround was extended and by how much for a flight that size. Simulated bags don't count, because the simulator runs faster than real time.

## Good to know

- **Live map.** Open **Live map** on the projector during the walk: every bag is a dot in the place it really is (green right place, amber needs attention, red wrong place, hollow expected, blue offloaded). Antennas pulse while reading. Type a passenger in **Track a bag** (or click their dot) to light up their route and show each step with times.

- **Reader test (diagnostics).** Shows the reader and its four antennas; every tag an antenna sees right now is a dot under it, and the list gives each tag's full EPC. It uses the raw reads (no RSSI filter), and POC scanning is paused while the page is open, so walking tags past the antennas doesn't check in, sort or load anything.
- **Rescanning a tag that hasn't moved.** A tag held still in a field counts as one scan (3 s de-duplication). To rescan without moving it, use **Ready to rescan** or move the tag away briefly.
- **The loading antenna starts disarmed.** This stops tags lying near antenna 3 from raising "unknown tag" alarms. **Manual scan** on the Loading page always works, even with no hardware.
- **Data.** The audit trail is append-only, enforced by database triggers. Data lives in `%LocalAppData%\KQ.Brs.Poc` (SQLite `brs.db`, `settings.json`, `errors.log`).
- **Type B formats** follow RP 1745 style but are POC-made. They still need checking against KQ/SITA samples (spec 9.4).
