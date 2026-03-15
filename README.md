<div align="center">
<h1>🪦 Graveyard Analytics for Jellyfin</h1>
<p>
  <img alt="Plugin Banner" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/header.png" />
</p>
<a href="https://github.com/jackwander/jellyfin-graveyard-analytics/releases">
<img alt="GitHub Downloads" src="https://img.shields.io/github/downloads/jackwander/jellyfin-graveyard-analytics/total?label=Github%20Downloads"/>
</a>
<p><b>Manage your server's afterlife.</b> Graveyard Analytics is a powerful, real-time dashboard and server-management plugin for Jellyfin (v10.11+). It identifies lifeless media, tracks your active users, highlights server bottlenecks, and allows you to permanently exorcise dead weight from your hard drives.</p>
<p>Stop guessing what your users are watching, and start reclaiming your storage space.</p>
</div>


---

## ⚠️ CRITICAL REQUIREMENT
**You MUST have the official [Playback Reporting](https://jellyfin.org/docs/general/server/plugins/) plugin installed and active for Graveyard Analytics to function.** This plugin reads directly from the Playback Reporting SQLite database to generate its real-time metrics. If Playback Reporting is not installed, Graveyard Analytics will not be able to track your media's vitality.

---

## 🦇 Features

Graveyard Analytics is split into four distinct, thematic pillars accessible directly from your Jellyfin Dashboard:

### 1. The Morgue (Unwatched Media)

<img alt="the-morgue" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/themorgue.png" loading="lazy" />

Identify the dead weight. The Morgue scans your entire library and isolates every movie and show that has **zero plays**. 
* See exactly how much storage space is being wasted by stagnant media.
* Sort by Size, Type, or Title.
* **Action:** Click **Condemn** to move an item to The Chapel for final review.

### 2. The Sanctuary (Living Media)

<img alt="the-sanctuary" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/thesanctuary.png" loading="lazy" />

Celebrate the living. The Sanctuary displays only the media that your users are actively engaging with.
* See the true storage footprint of your *watched* media.
* Sorts dynamically by **Vitality (Total Plays)** and **Reach (Unique Users)**.
* Displays the total formatted time users have spent watching specific items.

### 3. The Chapel (Condemned Items)

<img alt="the-chapel" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/thechapel.png" loading="lazy" />

The waiting room for the afterlife. Items you condemned from The Morgue or The Sanctuary are sent here and tagged with `[Chapel]`.
* **"Leaving Soon" Collection:** When an item is condemned, it is automatically added to a public **"Leaving Soon"** collection visible to your users, giving them one last chance to watch it before it gets deleted!

<img alt="leaving-soon-collection" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/thechapelcollection.png" loading="lazy" />

* **Action (Pardon):** Forgive the media and send it back to the general library (this removes it from the Leaving Soon collection).
* **Action (Exorcise):** Perform Last Rites. This will **permanently delete** the media files from your physical hard drive and remove them from Jellyfin.

### 4. The Guestbook (User Analytics)

<img alt="the-guestbook" src="https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/images/theguestbook.png" loading="lazy" />

A complete, unfiltered séance into your server's traffic. Monitor every single playback session, down to the 1-second false starts.
* **Timeframe Filtering:** Dynamically search by End Date and roll back up to 12 weeks.
* **Resource Vampires:** If a user triggers a Transcode, the method is highlighted in aggressive **RED**, allowing you to spot server bottlenecks instantly.
* **The Binge List:** A dynamic leaderboard showing your top 3 most active users in the selected timeframe.
* **The Ghosts:** Instantly identifies Jellyfin users who haven't watched a single second of media during your selected timeframe.

---

## 📦 Installation

To install Graveyard Analytics, add this repository to your Jellyfin server:

1. Open your Jellyfin Dashboard.
2. Navigate to **Plugins** > **Repositories**.
3. Click the **+** (Add) button.
4. Enter the following details:
   * **Name:** Graveyard Analytics
   * **URL:** `https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/manifest.json`
5. Go to the **Catalog** tab, find **Graveyard Analytics** under the Analytics section, and click Install.
6. **Restart your Jellyfin server.**

---

## 🛠️ Building from Source
If you wish to compile the plugin yourself:

1. Clone the repository.
2. Ensure you have the .NET 9 SDK installed.
3. Run the following command in the project root:
   ```bash
   dotnet publish -c Release
   ```
4. Place the resulting .dll files into your Jellyfin plugins directory.

---

## 📜 License
This project is licensed under the MIT License - see the [LICENSE](https://raw.githubusercontent.com/jackwander/jellyfin-graveyard-analytics/master/LICENSE) file for details.

---

---

## 🤖 AI Disclosure
**Graveyard Analytics** uses AI-assisted logic for its C# backend. 
* **Scope:** AI was utilized specifically for **C# code optimization, logic refinement, and resolving .NET 9 compatibility warnings.**
* **Oversight:** All architectural decisions, thematic design, and final code integrations were performed by the maintainer. Every line of code has been manually reviewed and tested to ensure stability within the Jellyfin 10.11.0+ environment.
