---
title: "Technical Spikes Explained (Simple English)"
area: phases
tags: [phase-0, spikes, explained, beginner, non-technical, rls, offline, signatures, postgresql]
source: "Phase 0 execution (WP-P0-10 … WP-P0-13)"
status: draft
last_updated: 2026-10-22
---

# Technical Spikes Explained (Simple English)

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 0 plan](plan.md) · [Phase 0 tracker](tracker.md)

This page explains, without jargon, **what a technical spike is**, **which four spikes we did in Phase 0**, **what we found**, and **why it matters for the business**. For the technical details, see the [spikes folder](../../../spikes/README.md).

---

## 1. What is a technical spike?

A **technical spike** is a **small, quick experiment** that answers **one risky question** *before* we spend months building the real product.

Think of building a house. Before pouring the foundation, an engineer drills a few holes to test the soil. They are not building the house yet, just checking that the ground can carry it. If the soil is bad, it is cheap to find out now and very expensive to find out after the house is built.

A spike is that test hole, for software:

| | Spike | Real product code |
|---|---|---|
| Goal | Answer one question ("Can this work?") | Deliver features to customers |
| Size | Small, days | Large, months |
| Code quality | Simple, thrown away afterwards | Production quality, maintained for years |
| Output | **Evidence**: numbers, test results, a yes/no answer | Working software |

**Important:** the spike *code* is thrown away, but the *lessons* are kept. In Phase 1 we rebuild the proven ideas properly.

### Why do spikes at all?

Karry makes some big promises to customers:

- "Your company's data is **never** visible to another company."
- "Field entries are **signed** and cannot be faked or changed later."
- "The tablet works **without internet** and **never loses data**."

If any of these promises turned out to be impossible with our chosen technology, we would have to redesign everything. Spikes check this **in a few days instead of discovering it after months**.

### How do we know a spike's answer is true?

Each spike has **automated tests**: small programs that check the result again and again, automatically.

- They run on our computer and also on **GitHub Actions** (GitHub's machines run them each time code changes), so anyone can see the results.
- We also **broke the code on purpose** (for example, removed a security rule) to make sure the tests notice. They always did. This proves the tests are actually checking something, not just saying "OK".

---

## 2. The four spikes we did

### Spike 1: Keeping each customer's data separate

**The question:** Karry is one system shared by many companies (that is what "SaaS" means). Can we be **sure** that Company A never sees Company B's data, even when hundreds of users are working at the same time?

**Everyday comparison:** an apartment building. Everyone shares the same building (our database), but each apartment has its own lock. We tested that every key only opens its own door, even when 50 people are walking through the corridors at once.

**What we did:** we put the "lock" inside the database itself, not only in our application. Even if our application code has a bug, the database itself refuses to show another company's rows. Then we hammered it with **50 companies working at the same time over 10,000 operations**.

**Result: ✅ 0 leaks.** No company ever saw another company's data.

**Speed cost:** the lock makes the database about **2.4 % to 4 % slower**, which is acceptable (our target was under 5 %).

**Problems we found (and fixed in our plans):**

1. The security rule as we had first written it in our architecture documents **crashed** in one situation (when a database connection is reused by a new user). We corrected the rule in the docs.
2. If the system says "I am Company A" as a separate message to the database, it costs about **14 % extra time**. If it says it *in the same message* as the first question, the cost almost disappears. Phase 1 must do it the second way.

> Finding these two problems now, in a spike, cost a few hours. Finding them after launch could have meant a security incident or a slow product.

---

### Spike 2: Signatures that cannot be faked

**The question:** when an operator records fuel, meter hours or a weighbridge ticket on a tablet, can we **sign** that entry so nobody can fake it or change it later, and can our server **check** the signature?

**Everyday comparison:** a wax seal on a letter. Each tablet has its own seal (a secret digital key). The tablet stamps every entry with it. The server can check the seal is real, but **nobody, not even someone holding the tablet, can copy the seal** to use elsewhere.

**What we did:**

- Created the secret key inside the browser in a way that makes it **impossible to export or copy**.
- Closed and reopened the browser **10 times**: the key was still there and still worked.
- Signed entries on the tablet side and checked them on the server side (written in a different programming language, .NET).

**Result: ✅ it works.** The key cannot be copied, survives restarts, and the server accepts genuine signatures and **rejects anything that was changed**, even by one character.

**Problems we found:**

1. Browsers and our server write signatures in **two different formats**, a bit like writing a date as 22/10 or 10/22. With the wrong setting, the server would have rejected **every** genuine signature. We now know the correct setting and recorded it.
2. If a user presses "clear site data" in the browser, the key is gone. This is normal browser behaviour, so Phase 1 must include a simple way to **register the tablet again**.

---

### Spike 3: Working offline without losing anything

**The question:** quarries often have bad or no internet. Can the tablet keep working offline, **store everything safely**, and then send it to the server **exactly once** when the connection comes back (no lost entries, no duplicates)?

**Everyday comparison:** an outbox tray on a desk. When the post office is closed, letters pile up safely in the tray. When it opens, every letter is sent, and if the same letter is accidentally handed over twice, the post office notices and only delivers it once.

**What we did:**

- Built a tiny test version of the field app.
- Recorded **1,000 entries and 50 photos** while "offline".
- Reloaded the page and restarted the browser in the middle: nothing was lost.
- Started the app with **no internet at all**: it opened normally.
- Turned the connection back on: everything was sent, **each entry exactly once**, even when we deliberately re-sent the same batch twice.

**Result: ✅ works on a desktop computer (Chrome).**

**Still to do:** test on a **real, cheap Android tablet** (the kind used on a quarry site), offline for 72 hours, with low battery, then sync over a slow 3G connection. The checklist is in the [Phase 0 tracker §3](tracker.md#3-manual-device-lab-checklist-wp-p0-10-remainder).

---

### Spike 4: Does our database have all the tools we need?

**The question:** Karry needs some extra database "add-ons":

| Add-on | What it is for, in Karry |
|---|---|
| **PostGIS** | Maps and locations (site boundaries, stockpiles, truck positions) |
| **TimescaleDB** | Large amounts of time-based data (machine sensors, weather, meter readings) |
| **pgvector** | Powers the AI assistant's search |
| **pgcrypto** | Security helpers (hashing, encryption) |

Do they all work together on the latest database versions (PostgreSQL 17 and 18)?

**Everyday comparison:** checking that all the attachments (bucket, hammer, ripper) actually fit the excavator model we plan to buy.

**What we did:** a script starts each database version and installs all four add-ons, on GitHub's machines.

**Result: ✅ all four add-ons work on both PostgreSQL 17 and 18.**

**Still to do:** once you choose a hosting company (Phase 0 decision WP-P0-14), confirm that **their** managed database offers the same add-ons. Not every provider supports all of them.

**Small problem we found:** our check failed **once at random**. The reason: the database restarts itself once during its first start-up, and our script sometimes talked to it during that restart. We fixed the script to wait for the real start, then ran it **4 times in a row**, all green. We will use the same rule in Phase 1.

---

## 3. Summary

| # | Question | Answer | Still open |
|---|---|---|---|
| 1 | Is each company's data kept separate? | ✅ Yes, 0 leaks; about 2–4 % slower | Nothing; follow the rules in Phase 1 |
| 2 | Can tablet entries be signed so nobody can fake them? | ✅ Yes | Re-registration flow in Phase 1 |
| 3 | Can the tablet work offline without losing data? | ✅ Yes, on desktop Chrome | Test on a real cheap Android tablet |
| 4 | Does the database have all the add-ons we need? | ✅ Yes, on PostgreSQL 17 and 18 | Confirm with the chosen hosting provider |

**Automated test results (on GitHub Actions):** 15 of 15 server tests and 10 of 10 browser tests pass, and the database add-on checks pass on both versions.

## 4. What this means for the business

- **The four biggest technical risks are now low.** The core promises of Karry (data isolation, tamper-proof records, offline work) are technically possible with our chosen tools.
- **Five mistakes were caught early**: the crashing security rule, the slow way of identifying the company, the signature format mismatch, the lost key after "clear site data", and the random start-up failure. Each one was cheap to fix now and would have been costly later.
- **The plan is safer to sell.** When a customer asks "can another company see my data?" or "what if there is no internet?", we have tested evidence, not just a promise.

## 5. Where to find more

| You want… | Go to |
|---|---|
| The technical details and numbers of each spike | [spikes/README.md](../../../spikes/README.md) and each spike's own README |
| The status of all Phase 0 work | [Phase 0 tracker](tracker.md) |
| The Phase 0 plan and exit checklist | [Phase 0 plan](plan.md) |
| The security threats these spikes protect against | [Threat model](../../02-architecture/threat-model.md) |
| The architecture decisions that use these results | [ADR index](../../adr/README.md) |
