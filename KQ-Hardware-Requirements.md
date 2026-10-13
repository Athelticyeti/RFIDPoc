# KQ Baggage PoC: Hardware Requirements

Oct 13, 2026 · @Shane Crompton

## Purpose and constraints

The PoC needs hardware for three scan points at JKIA Terminal 1A (check-in desk, sorter tunnel, ramp) to track about 700 bags on one KQ flight. This doc starts with the check-in desk.

Requirements that drive the hardware choice:

- **The licence plate goes on the chip.** Each tag must carry the bag's 10-digit IATA licence plate, encoded to IATA RP 1740c, at bag drop (deck slide 5; repo spec section 3.4).
- **The PoC runs alongside the live system.** The deck calls for "non-intrusive integration alongside Kenya Airways DCS and airport baggage handling infrastructure". Live check-in printers, the DCS and the BHS are not modified.
- **There are three scanner positions.** 1 desk validation scanner, 1 fixed conveyor tunnel reader and 1 ruggedised ramp handheld.
- **The read rate must be above 99%** at every fixed scan point.

Sources: [Holomatrix PoC deck](https://assets.ocmo.co.za/kenya-airways-baggage-tracking-poc.html) (September 2026) and `KQ-Baggage-Tracking-POC-CSharp-Spec.md` in the RFIDPoc repo.

## Check-in desk, step 1: writing the licence plate

**Recommendation: Option A for the PoC.** The agent sticks an RFID label on the printed bag tag. A handheld scanner-encoder then reads the barcode and writes that licence plate into the label. It writes the licence plate at bag drop and doesn't touch the live check-in system. Option C is the production target.

The conveyor does not write tags. At the belt, a reader can also reach the next desk's bag and old tags from earlier trips. A wrong read can be filtered out, but a wrong write puts the wrong licence plate on a bag.

| Option | How it works | Touches live system | Extra agent work | Production-like | Hardware per desk |
| --- | --- | --- | --- | --- | --- |
| **A. Sticker, scan and write** (recommended) | Live printer prints the normal tag; agent sticks on an RFID label; one trigger pull scans the barcode and writes the chip | No | About 3–5 s per bag | Partly | Handheld UHF scanner-encoder with barcode scanner (e.g. Zebra RFD40 or RFD90 sled), or a desk encoding pad + USB barcode scanner |
| B. Second PoC printer | PoC prints and writes an RFID label for each BSM received; agent sticks it on the bag tag | No | Matching the right label to the right bag | Partly | RFID label printer (e.g. Zebra ZD621R) + a live BSM feed |
| C. Inlaid stock, live printer writes | Bag tag stock with a chip inside; an RFID module in the printer writes the licence plate as it prints | **Yes** | None | **Yes** | RFID module or RFID-capable bag tag printer (e.g. IER, VidTroniX) + RFID stock |

Ruled out: pairing a pre-encoded sticker to the licence plate without writing it. The chip wouldn't carry the licence plate.

For Option A:

- The label goes on the flat part of the bag tag, not the loop that sticks to itself round the handle.
- If the agent sticks a label on but forgets to write it, the conveyor reader in step 2 catches it.
- If reusable tags are trialled for Business and SkyPriority, the same device rewrites them at the desk.

## Check-in desk, step 2: validation reader at the desk conveyor

A fixed reader at the weigh or injection belt reads the tag once the bag is on the belt. It confirms that the expected licence plate is on the bag going into the BHS (FR-02, status `CheckedIn`). It only reads.

| Item | Requirement |
| --- | --- |
| Reader | Desktop UHF reader, or a second Alien ALR-9900+ with 1 near-field antenna (repo spec, open question) |
| Antenna | 1 per desk, under or beside the weigh belt, low power |
| Cross-read control | Must not read the next desk's belt; use power settings, antenna position and shielding, and calibrate on site |
| Filtering | Picks out the expected licence plate and ignores old tags from earlier trips |
| Read rate | Above 99% |
| Connection | Ethernet to the PoC network (VLAN), separate from live check-in equipment |

One four-port reader such as the ALR-9900+ can serve up to four desks, one antenna each, if the desks are close enough for antenna cables.

## Tags and consumables

The trial needs about 700 writable UHF labels for economy bags, plus spares for failed writes and bench testing.

| Item | Requirement |
| --- | --- |
| Air interface | UHF, EPC Gen2, writable EPC memory for the RP 1740c licence plate encoding |
| Form (Option A) | Self-adhesive label sized to the flat part of the bag tag; tested on bag tag paper and on bags |
| Form (Option C, later) | Bag tag stock with an inlaid chip, in the format the desk printers take (about 400 × 50 mm, to be confirmed) |
| Quantity | 700 for the trial + spares (to be decided) + 50 for bench testing (repo spec) |
| Reusable tags | Only if KQ decides to trial them for Business and SkyPriority (the deck says "assessed") |

Tag supply is to be covered in the separate PoC agreement (deck slide 10).

## Sorter tunnel and ramp (to be detailed)

These are the repo spec's current positions; we'll detail them next.

| Scan point | Device | Antennas |
| --- | --- | --- |
| Sorter tunnel (Belt 04) | Alien ALR-9900+ EMA | 4, circularly polarised, around the belt |
| Ramp / hold (AK7, AK8, Hold 2) | Rugged Android UHF handheld | Built in |
| Destination (EBB carousel) | Handheld or fixed reader, outside the deck's three positions | 1–2 |

## Open questions

- [ ] How many check-in desks will handle the trial flight? The deck has one desk scanner, but 700 bags are unlikely to go through one desk. This sets how many encoders and desk antennas to buy.
- [ ] Desk validation reader: a second ALR-9900+ or a cheaper desktop reader?
- [ ] Tag supplier, chip model and the exact RP 1740c encoding of the licence plate.
- [ ] Label size, and where it goes on KQ's bag tag stock.
- [ ] Will KQ trial reusable tags for Business and SkyPriority?
- [ ] Is Option C (live printer writes) worth raising now with the airport or common-use provider, since approval is likely to take the longest?
