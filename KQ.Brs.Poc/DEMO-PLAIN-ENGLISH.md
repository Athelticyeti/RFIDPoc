# KQ-504 RFID demo guide

## Description

The demo follows a bag from the check-in desk to the aircraft hold, tracked by a small radio chip in its bag tag. At each stop a reader picks up the chip without anyone scanning a barcode, and the computer checks the bag is going where it should. If a bag is about to be loaded onto the wrong plane, or belongs to a passenger who shouldn't fly, the screen sounds an alarm and stops it.

Everything runs on a desk: one reader, four antennas standing in for places in the airport, and real tags you carry from antenna to antenna. It's a first look to help decide what equipment to buy for a proper lab, so expect it to be a bit rough around the edges.

## Terminologies

| Word | What it means |
| --- | --- |
| RFID tag | A sticker with a tiny chip and aerial in it. A reader can pick it up by radio from a short distance, with no line of sight needed. |
| Reader and antennas | The grey box is the reader. The four flat panels plugged into it are its antennas: each one "listens" for tags in front of it. |
| Licence plate | The 10-digit number on every bag tag, e.g. 0706200001. It's the bag's ID. In the demo it's written into the chip. |
| Type B message | The standard short text message airlines and airports have used for decades to tell each other about passengers and bags. It's plain text with one fact per line, e.g. `.N/0706200001001` for the bag's licence plate. BSM, BPM and CHG below are all Type B messages. In the app, the **Type B messages** page shows them: **Inbox** is what the airline sent us, **Outbox** is what the baggage system sent back. |
| BSM | Baggage Source Message. When a passenger checks in, the airline's check-in system sends this message to say "this passenger has this bag, with this licence plate, on this flight". It's how the baggage system learns a bag exists. |
| BPM | Baggage Processed Message. The baggage system sends it when a bag is sorted or loaded: "bag X was seen here". |
| CHG / DEL | A change to, or cancellation of, a BSM. For example, the passenger didn't board, or their ticket is no longer valid. |
| ULD | Unit Load Device: the metal container bags go into before it's loaded into the plane's hold. The demo has two, AK7 and AK8. |
| Ramp | The area around the aircraft where bags are loaded. |
| Pushback | The moment the aircraft is pushed away from the gate. All bags must be sorted out before then. |
| Supervisor override | A supervisor clears an alarm by entering a PIN (1234) and a reason, instead of fixing the bag. It's logged. |

## The four antennas

Each antenna pretends to be one place in the airport. Moving a tag from one antenna to the next is the bag's journey.

```
 +---------------+     +----------------+     +-------------------+     +-------------------+
 |   Antenna 0   |     | Antennas 1 + 2 |     |     Antenna 3     |     |    no antenna     |
 | Check-in desk | --> | Sorting tunnel | --> |   Loading point   | --> | Container AK7/AK8 |
 |  tag linked   |     |  bag Sorted    |     | load / DO NOT LOAD|     |    bag Loaded     |
 +---------------+     +----------------+     +-------------------+     +-------------------+
```

A bag for the wrong flight is caught in the tunnel; the final load check is at the loading point.

| Antenna | Pretends to be | What happens when a tag is held there |
| --- | --- | --- |
| 0 | Check-in desk | The tag is linked to the passenger's bag (or checked against it). |
| 1 and 2 | Sorting tunnel (two antennas facing each other across the "belt") | The bag is marked **Sorted**. |
| 3 | Loading point at the aircraft (a handheld scanner in real life) | The computer decides: load it, or **DO NOT LOAD**. |

Hold **one tag at a time**, close to the antenna you mean. Keep the other tags in the metal tin, or the antennas will read them too.

## Reading the Dashboard

Each bag is one row. Two columns tell you where it is and whether it's OK.

**CHECK-IN · SORT · LOAD:** three dots that fill in as the bag passes the check-in desk, the sorting tunnel and the loading point. Three filled dots = the bag is on the plane.

**STATUS:** one badge that sums up the bag right now.

| Badge | What it means |
| --- | --- |
| EXPECTED | The airline has told us about the bag (its BSM arrived), but no antenna has read its tag yet. |
| IN SCAN | An antenna is reading the tag right now. It changes back after about 3 seconds. |
| MATCHED | All good: the tag has been read and the bag is where it should be. The dots show how far it has got. |
| HELD | The bag is checked in, but its passenger's ticket isn't valid or they haven't boarded. It mustn't be loaded. |
| EXCEPTION | Something is wrong and needs dealing with, e.g. a bag for the wrong flight or one that was refused at loading. It's listed on the **Exceptions** page. |
| MISSING | The bag skipped a step: it reached the loading point without being read in the sorting tunnel, or the pre-pushback check found it checked in but not loaded. |
| OFFLOADED | The bag was taken off the flight, because the passenger didn't board or their booking was cancelled. |

The steps below say a bag "turns CheckedIn", "Sorted" or "Loaded". On the Dashboard you'll see that as the dots filling in, with the badge showing **MATCHED**. Click a bag to see those words in its details.

## Before you start

Allow 10 minutes before the audience arrives.

- [ ] **Close any other reader program** (e.g. RFIDPoc). Only one program can talk to the reader at a time.
- [ ] **Put all spare tags in the metal tin**, away from the antennas.
- [ ] **Start the app** (KQ.Brs.Poc). The reader light at the bottom left of the app should turn **green**. If Windows asks about the firewall, click **Allow**.
- [ ] **Start with an empty flight:** Settings → **Clean database** → tick "I understand" → **Delete everything**.
- [ ] **Set the antennas:** Settings → Antenna → **Set POC defaults**. This also switches on the loading antenna (3).
- [ ] **Check Settings → Show simulator is off**, so only real tags are used.
- [ ] Optional: open **Live map** on a second screen or projector. It shows each bag as a dot moving through the airport.

## The demo, step by step

One bag, start to finish, takes about 3 minutes. Each step says what to do, then what to point out.

### 1. A passenger checks in

**Function:** on the **Dashboard**, click **Add passenger**. Type a surname and initial (a volunteer from the audience works well), leave 1 bag and **Ticket valid** on, then confirm.

**Description:** "This is the airline's check-in system telling the baggage system about the bag. That message is called a BSM." You can show it under **Type B messages → Inbox**: each line of the message is one fact about the bag, such as the flight, the licence plate and the passenger's name. The bag appears on the Dashboard as **EXPECTED**: the system knows it's coming but hasn't seen it yet.

### 2. The bag tag is printed

**Function:** a window pops up asking you to write the tag. Take **one** tag from the tin, hold it still on **antenna 0**, and click **Write tag**.

**Description:** "In an airport the desk prints a bag tag with a chip in it. Here we write the bag's number straight into the chip, then read it back to check." Keep the tag there.

### 3. Check-in confirmed

**Function:** nothing extra. While the tag is still at antenna 0, the app reads it.

**Description:** "The desk antenna has read the chip and matched it to the passenger's booking." The bag turns **CheckedIn** on the Dashboard.

### 4. Through the sorting tunnel

**Function:** carry the tag slowly between **antennas 1 and 2**.

**Description:** "Behind the scenes the bag goes down a conveyor through a tunnel of antennas. Nobody has to scan it." The bag turns **Sorted**, and a BPM ("bag seen here") appears under **Type B messages → Outbox**.

### 5. Loading onto the aircraft

This step stands in for a baggage handler at the plane with a handheld scanner. Antenna 3 is that scanner. The **Loading · Hold 2** page is the handler's screen.

1. Open the **Loading · Hold 2** page.
2. At the top, click **ULD AK8**. This is the container the handler is filling.
3. Check the **Loading antenna (3) armed** switch says **Scanning**. If it says "Ignoring reads", switch it on. (It starts off so that tags lying near antenna 3 don't set off alarms.)
4. Hold the tag on **antenna 3**.
5. The big panel turns green: **LOAD → AK8**. The bag is now **Loaded**.

**Description:** "Before any bag goes into the hold, the system checks it's on this flight, the passenger is travelling and the ticket is valid. Green means go."

### 6. Show the bag's history

**Function:** on the **Dashboard**, click the bag.

**Description:** "Every step is recorded with a time and place, and the record can't be changed afterwards." The **Live map** shows the same journey as a dot moving through the airport.

## Showing what happens when something is wrong

This is the part the audience remembers. When a bag must not fly, the screen goes red with **DO NOT LOAD**, an alarm sounds, and the app is locked until someone deals with it. There are two ways to clear it:

- **Fix it:** take the bag aside, click **Ready to rescan**, then hold the tag on **antenna 3** again. The system sees it was dealt with and unlocks.
- **Supervisor override:** a supervisor enters the PIN **1234** and a reason. It unlocks, and the override is recorded.

Pick one or two of these, not all three.

### Bag for the wrong flight

**Set up once:** Settings → **Show misroute demo** on → **Save**.

1. Add a second passenger and check in their bag (steps 1 to 3 above).
2. **Move the tag away from antenna 0.** Then on the **Dashboard**, click that bag and choose **Flag tag as KQ-412 (misroute demo)**.
3. **What you'll see:** the tag now belongs to a **new passenger** on flight KQ-412 to Dar es Salaam, with a made-up name (e.g. P. Omondi). Your passenger goes back to "awaiting check-in". On the Live map, their dot slides back and a red dot for the KQ-412 passenger appears under **Checked in**.
4. **Description:** "Someone at check-in put a Dar es Salaam tag on a bag going to our KQ-504 conveyor."
5. Carry the tag through the **sorting tunnel** (antennas 1 and 2). A red label appears by the tunnel on the Live map, the red dot slides up into **Problems in the hall**, and the full-screen alarm fires.
6. Click **Ready to rescan**, then hold the tag on **antenna 3**. The alarm clears, the dot moves to **Refused at loading**, and the record says the bag was taken off and re-routed.

**Description:** "A bag heading for the wrong plane is caught before it gets anywhere near the aircraft."

Why move the tag away first: if it's still on antenna 0 when you flag it, the desk reads it again as a wrong-flight bag, which muddles the story. To reset afterwards, click the KQ-412 bag and choose **Undo KQ-412 flag**: the tag goes back to your passenger.

### Ticket not valid

1. Click **Add passenger**, but switch **Ticket valid** off.
2. Write the tag, check in, and go through the tunnel as normal. Nothing stops it yet.
3. On the Loading page, hold the tag on **antenna 3**. The red alarm fires: **No authority to load (ticket not valid)**.
4. Clear it with **Supervisor override** (PIN 1234 and a reason).

**Description:** "The airline can say 'don't load this bag' at any time, and the loading point enforces it."

### Passenger didn't board

Use a bag that's already **Loaded**.

1. Open **Type B messages** and click **Example: no-show CHG**. This fills in a message from the airline saying the passenger didn't board.
2. Check the line starting `.N/` shows the loaded bag's number, then click **Apply**.
3. The red alarm fires: the bag has to come off the plane.
4. Click **Ready to rescan** and hold the tag on **antenna 3** to confirm the bag was taken off.

**Description:** "If a passenger doesn't turn up, their bag must not fly without them. The system knows exactly which container it's in."

## Finishing up

### Ready for the plane to leave?

**Function:** on the **Dashboard**, click **Pre-pushback check**.

**Description:** "Before the plane leaves, one click shows whether every checked-in bag is on board and nothing needs to come off." Green **Clear for pushback** means all is well; the summary reads e.g. "1 of 1 bag(s) loaded". Amber **Not clear** lists the bags left behind or to be taken off.

To show amber: add a passenger and check in their bag, but don't load it, then run the check again.

### The report

**Function:** open **Reports**.

**Description:** "This is what we'd use to judge the trial: how reliably each antenna read the tags (the target is over 99%), how many problems came up and how fast they were fixed." **Export** saves it as a file.

The **Turnaround vs baseline flight** box compares loading speed with a normal flight. It only means something in a real trial, so skip it in this demo.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| Reader light at the bottom left isn't green | Close any other reader program, check the reader's network cable, and allow the app through the Windows firewall. |
| Write tag says "2 tags at antenna 0" | Another tag is nearby. Put it in the tin and try again. |
| Write tag says "No tag at antenna 0" | Hold the tag flat and still on the antenna, then try again. |
| "This tag already belongs to…" | That tag is already another passenger's bag. Use a fresh tag from the tin. |
| Nothing happens at antenna 3 | On the Loading page, switch **Loading antenna (3) armed** on. |
| A tag shows up at the wrong antenna | Antennas on a desk can hear each other. Hold the tag closer to the right one and keep the others away. **Reader test** shows exactly which antenna sees what. |
| Holding a tag still doesn't trigger anything again | A tag that doesn't move counts once. Move it away for a few seconds and bring it back. |
| The red alarm won't go away | Click **Ready to rescan** and hold the tag on antenna 3, or use **Supervisor override** with PIN 1234. |

The full technical guide is DEMO.md in the same folder.
