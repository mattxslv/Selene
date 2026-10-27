# How Selene is developed

Selene is a personal passion project that I chose to share publicly. It began with the fullscreen Playnite experience I wanted for myself, and I want to keep enjoying both using it and working on it.

Feedback, bug reports, and ideas are welcome. Sharing an idea doesn't create a commitment to implement it, and popularity alone doesn't decide the direction of the project. Selene will grow when I have the time and motivation, at a pace that keeps it enjoyable.

## Priorities

- **Bugs and stability first.** I aim to investigate reproducible issues and compatibility problems as time allows. Reports with steps to reproduce, logs, and system details are especially helpful. Reporting a bug doesn't guarantee an immediate fix.
- **Polish matters.** Performance, consistency, accessibility, and refining existing features are worthwhile updates even when nothing new is added.
- **Features are optional.** I may pick up suggestions that fit Selene and excite me, or explore ideas of my own. I won't promise features just because they've been requested.
- **No fixed schedule.** Releases happen when changes are ready, not on a deadline or because a release must contain a certain number of features. It's also fine for the project to be quiet for a while.

## What "done" means

For features and fixes I choose to ship:

1. They work in a fresh Playnite with the packaged theme and add-on (`tools/fresh-test.ps1`), not only on my own setup.
2. New features follow Selene's existing conventions where relevant: optional switches, motion and sound feedback, and colors from the selected palette.
3. They have a user-facing note in `CHANGELOG.md` under "Unreleased".

Before a release, the theme and add-on must package with `tools\package.ps1`. Known serious regressions should be addressed rather than knowingly shipped. Releases are published as GitHub `v<version>` tags (see [Building](docs/guide/building.md), "Releasing").

## How requests are handled

When considering a request, I look at:

1. **Is something broken?** Reproducible bugs generally take priority over new features.
2. **Does it fit Selene?** The project is a calm, console-style front end for Playnite.
3. **Is it worthwhile to build and maintain?** Each feature adds complexity and ongoing maintenance.
4. **Do I want to work on it?** This is a hobby project, so personal interest and available time matter.

A request might be marked **Planned** (something I've chosen to work on), **Later** (an idea I may revisit), or **Not planned** (not something I intend to add). "Later" is not a promise or a queue, and there's no obligation to choose features from open issues for the next release.

Constructive pull requests are welcome, but please open an issue first for significant changes. A proposed implementation isn't a guarantee of acceptance; it still needs to fit Selene and be maintainable.

## Current direction

As of v1.3.1, the focus is on supporting the existing experience, fixing bugs, and polishing what is already there. Future feature work will be chosen as interest and time allow; there is no committed v1.4 feature list.

## Not planned

Requests closed as not planned can be found under the [`not planned`](https://github.com/mattxslv/Selene/issues?q=label%3A%22not+planned%22) label.
