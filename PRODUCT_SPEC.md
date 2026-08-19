# Game Session Scheduler — Product Specification

**Status:** Product requirements complete — ready for technical design
**Last updated:** 2026-07-29
**Scope of this document:** Product behavior and constraints. Technology choices will be recorded only after the product requirements justify them.

## 1. Product summary

Game Session Scheduler helps friends find a workable time and organize a multiplayer game session.

The product is intended to support:

- A web interface first.
- Android and iOS clients later.

## 2. Problem statement

Existing friend groups struggle to schedule multiplayer sessions because their free time rarely lines up, people fail to respond, choosing a time requires too much back-and-forth, and not everyone participates in the same group chats.

The product should give the whole friendship network a shared place to publish availability, propose actual sessions, and respond to them. It is for coordinating existing friends, not discovering strangers to play with.

## 3. Goals

### Confirmed

- Let friends coordinate multiplayer game sessions.
- Deliver the first usable product through the web.
- Preserve a path to Android and iOS clients.
- Make it easy to compare participants' availability.
- Keep session details and attendance status in one place.
- Reduce coordination messages outside the app.
- Allow useful sessions to be created even when the game has not been chosen.
- Account for participants in different time zones.
- Improve response rates without excessive or unwanted notifications.

## 4. Non-goals

- Public matchmaking or player discovery with strangers.
- Replacing the existing real-world friendship network.

Also outside the initial release:

- Voice or text chat.
- Launching games or joining lobbies directly from the product.
- Tournament, league, or esports management.
- General-purpose event scheduling outside games.

## 5. Users and relationships

The initial audience is people in existing friend groups whose schedules make it difficult to find time for multiplayer games. The service will be publicly available and free to use, but interactions are intended to remain within existing friendship networks.

Confirmed:

- People can have friends in the product.
- Friendship is a mutual connection accepted by both people.
- Anyone can register directly; invitations are an additional entry path, not a registration gate.
- Friends can see one another's shared availability. Per-block visibility restrictions are outside the initial release.
- A session organizer can directly invite friends.
- A **public** session lets the organizer, pending invitees, approved participants, Going and Maybe participants, and waitlisted registered users extend visibility to their friends; unapproved join requesters do not. A **Groups** session instead grants access only to a targeted friend group's Accepted members and owner (D-198), with no such friend-of-participant extension. A declined invitation, rejected request, or departed participant no longer extends visibility either way. (amended for D-198)
- Anyone who can see a session can see its attendee list and individual responses.
- Anyone who can see a session can also see its notes before requesting to join.
- A session can instead be private. It is visible to its invitees plus any participants and outstanding requesters retained when an existing session becomes private.
- If a visible session becomes private, existing non-invited participants and outstanding requests remain, but friends who only had access through those people immediately lose access.
- A shareable link can be used to invite someone.
- A link invitee can respond as a guest without creating an account; only a display name is initially required.
- A guest can revisit the same link to revise their response.
- Every guest receives a unique invitation link. It expires after the session, is revoked immediately if the session is canceled, and cannot be renewed.
- Guests receive no notifications through any channel. A guest sees the current session state, including cancellation, only by revisiting their link.
- Private sessions can use guest links. A guest link is a transferable bearer credential: forwarding it gives the recipient control of that single guest identity and response.
- A private fixed-time session can be created using only guest links. The same guest link may be used on multiple devices; edits use last-write-wins until revocation or expiry.
- An organizer can remove confirmed or pending guests; removal immediately revokes that guest's link.
- Guests do not extend inherited session visibility because they do not have a registered friendship graph.
- Users can find one another through profile links, QR codes, and friend codes. QR codes use the stable friend code rather than the changeable username.
- Users can import their Steam friend list. Other platform imports may be added later.
- Steam import suggests matched registered users for mutual friend approval and offers optional invitation links for unmatched friends.

The minimum registered-user profile contains:

- Display name.
- Unique username.
- Avatar.
- Linked gaming platforms and handles.
- Specified time zone.

Usernames are unique and case-insensitive. A user may change their username at most once every 30 days. Existing username-based profile links stop working immediately after a change, while the stable friend code remains unchanged. The former username is reserved for 30 days after the change and then becomes available again. Display names do not need to be unique.

User-uploaded avatar images are not allowed. A linked platform account, initially Steam, can supply the avatar; when several accounts are linked, the user selects the active platform avatar. An unlinked account can select one emoji as its avatar.

Blocking is required in the initial release. A blocked user cannot discover the blocker or gain access to new sessions through the blocker's participation. Existing shared sessions organized by a third person are the exception described below. Comments are outside the product. Mobile-store reporting requirements for the remaining user-authored profile and session fields are tracked in section 8.6.

Blocking is mutually invisible and removes any existing friendship. In a session organized by a third person, both users retain access but each sees **Anonymous User** in place of the other. If a participant blocks an organizer, the participant leaves that organizer's sessions. If an organizer blocks a participant, that participant is removed from the organizer's sessions.

To be defined:

- Persistent named groups are outside the initial release but may be added later.
- How duplicate guest names are handled.
- How a guest is reconciled with a later registered account.
- Long-term friend-code privacy and whether regeneration may be added after MVP.

## 6. Core concepts

The following working terms will be refined during discovery:

- **Organizer:** A person who proposes or creates a session.
- **Participant:** A person invited to or involved in a session.
- **Session:** A planned multiplayer gaming event.
- **Availability:** Times at which a participant says they can play.
- **Group:** A potentially reusable set of friends who play together.
- **Game:** The title or activity planned for a session.

## 7. Core user journeys

### 7.1 Create a session at a fixed time

1. An organizer creates a session for a specified start time and duration.
   The organizer begins as Going but may later change their own attendance without transferring ownership.
2. The organizer supplies a required title and minimum attendance and may specify a game, platform, notes, and maximum capacity. The game and maximum capacity are optional.
3. The organizer invites friends, including through a shareable invitation link.
   A **public** session requires at least one directly invited registered friend when created; a **Groups** session instead requires at least one targeted friend group (D-198); a guest link alone satisfies neither requirement. (amended for D-198)
4. Registered or guest invitees respond **Going**, **Maybe**, or **Declined**.
5. Unless the session is private, eligible friends or group members can see it and request to join — a **public** session extends this through friend-of-participant inheritance, a **Groups** session extends it to the targeted friend group's Accepted members and owner (D-198). (amended for D-198)
6. Every join request requires organizer approval. A requester does not choose an attendance intent; approval marks them Going, or Going-waitlisted if capacity is full. Invitations can also be added after creation.
7. The organizer and Going guests count toward minimum attendance. Both Going and Maybe participants consume optional maximum capacity.
8. When optional maximum capacity is reached, new Going or Maybe responders enter the waitlist with their selected attendance status.
9. If capacity becomes available, the organizer chooses whom to promote. Promoting either a Going- or Maybe-waitlisted participant fills the slot immediately while preserving their attendance status.
10. When the number of Going participants reaches the configured minimum, the session becomes Confirmed automatically.

If the organizer changes the session's start time before the session starts, confirmation is cleared and every participant, including waitlisted participants, returns to Pending. After the session starts, the start time and minimum attendance can no longer be edited. Changes to other details do not reset responses.

Going and Maybe participants can leave a session. If Going attendance falls below the minimum, the session remains Confirmed while any Going-waitlisted candidate remains; Maybe-waitlisted candidates do not preserve confirmation. It becomes unconfirmed only when no Going-waitlisted candidate remains. After the start time, a Confirmed session remains Confirmed and will Complete even if attendance falls below minimum; an unconfirmed in-progress session may still Confirm if Going reaches minimum before the end. Only the organizer can cancel a session, including after it becomes Completed. Cancellation is immediate, removes the session from history, notifies its registered participants, and immediately revokes every guest link. A Confirmed session becomes Completed automatically after its end time unless it is later canceled; an unconfirmed past session becomes Expired. Only non-canceled Completed sessions remain in history.

### 7.2 Share availability

1. A user publishes one-time or recurring blocks during which they are available.
2. Eligible friends can see those availability blocks.
3. The user selects friends and a date/time range.
4. The product searches the next seven days by default, calculates continuous full and partial overlap windows, and ranks them by the number of selected friends who are available, using Maybe count to break ties. All selected friends are optional; desired session duration is not an input and results are not artificially limited.
5. A recurring block follows a weekly schedule. The owner can delete one occurrence or the entire recurring series.

Availability uses specific time blocks and is visible to all friends in the initial release. Overlapping blocks are visually merged and treated as one continuous interval. A recommendation can create either a prefilled fixed-time session or a prefilled poll. The organizer may choose a duration beyond the recommended overlap and receives a warning.

### 7.3 Poll multiple candidate times

1. An organizer supplies the eventual session details, creates a poll containing multiple possible start times, and chooses a 6-, 12-, 24-, or 48-hour voting deadline. Each candidate option carries its own start time **and its own duration**; every other eventual session detail is shared across the options (D-211).
   The default deadline is 24 hours and at least two initial options are required. Candidate times are not required to occur after the voting deadline.
2. Registered or guest invitees mark each option **Available**, **Maybe**, or **Unavailable**. Voters can suggest additional times.
   Suggested times become poll options immediately without organizer approval.
3. Identical date-times are deduplicated and a poll can contain at most 12 options. Suggesting an option beyond the limit fails with a clear error. The organizer can delete any option without notifying its voters.
4. Voting closes at the deadline and waits for the organizer to select an option; there is no automatic winner. If no option is selected within 48 hours after the deadline, the poll expires and its registered participants are notified.
5. The selected option becomes the session's fixed start time, and its own duration becomes the session's duration (D-211). Available voters become Going, Maybe voters remain Maybe, and Unavailable voters become Declined, except that the organizer becomes Going regardless of their vote.

Candidate options remain visible and selectable after their start times pass. A poll does not expire early when every option has passed; its normal voting deadline and 48-hour organizer-selection window still apply.

If the chosen option has started but not ended, finalization creates an in-progress session. If it has already ended, finalization immediately evaluates its mapped Going count and creates a Completed result when the minimum is met or an Expired result otherwise. An Expired result is not retained in history.

A **public** poll follows the same inherited visibility rules as a fixed session and requires at least one directly invited registered friend. A **Groups** poll instead requires at least one targeted friend group at creation (D-198) and grants that group's Accepted members and owner the same see-and-request-to-join access. Eligible friends or group members can request to join while it is Polling; approval makes them a voter with access to all current options. A private poll may instead be created using only guest links. (amended for D-198)

When a poll is finalized with more Available and Maybe voters than maximum capacity, allocate active capacity in this order: organizer, Available voters ordered by response time, then Maybe voters ordered by response time. Overflow enters the matching Going or Maybe waitlist state. A voter's response time is the timestamp of their first submitted poll response and does not change when they revise their votes.

## 8. Functional requirements

### 8.1 Scheduling

Confirmed:

- A session may be created directly for a fixed start time.
- A session has a duration in 15-minute increments, with a minimum of 15 minutes and a maximum of 24 hours.
- Fixed-session defaults are minimum attendance 4, maximum capacity 4, two-hour duration, platform N/A, and **public** visibility (D-198's three-level `Public | Private | Groups`). The organizer may change these values and may clear maximum capacity for unlimited attendance. (amended for D-198)
- The initial release does not let users save custom session-creation defaults.
- A session must start at least 15 minutes in the future and no more than one year in the future.
- Invitees can respond **Going**, **Maybe**, or **Declined**.
- Users can publish one-time or recurring availability blocks.
- Availability blocks use exact start and end times and are visible to all friends in the initial release.
- Availability is created by dragging or selecting on the week calendar in 15-minute increments and then choosing one-time or weekly recurrence. An accessible keyboard/non-drag alternative is required.
- Recurring availability follows a weekly pattern and supports deleting one occurrence or the whole series.
- Weekly availability continues until deleted. Editing defaults to **Only this occurrence**. Selecting **Apply to all future occurrences** changes the selected occurrence and every later occurrence while leaving earlier occurrences unchanged.
- The product calculates availability overlaps and recommends times.
- An organizer can poll invitees about multiple candidate times.
- Poll responses are Available, Maybe, and Unavailable; organizers, invitees, and guests can vote on every option and all voters can suggest times.
- Poll deadlines are organizer-selected durations of 6, 12, 24, or 48 hours.
- The poll default is 24 hours and creation requires at least two initial options. Options may occur before the voting deadline.
- Poll options remain visible and selectable after their start time. A poll does not expire early merely because all options have passed.
- Polls support at most 12 distinct date-time options. Duplicate date-times are merged, suggesting an option beyond the limit fails with a clear error, and the organizer can delete any option without notifying its voters.
- At the deadline, voting closes and the organizer selects the final option manually. The poll expires after 48 additional hours without a selection, notifying its registered participants.
- **Public** polls use normal inherited visibility, accept join requests during Polling, and require one directly invited registered friend; **Groups** polls instead require one targeted friend group at creation and grant its Accepted members and owner the same request-to-join access (D-198). Approved requesters become voters with all current options. (amended for D-198)
- Private polls can be created with guest links only.
- Poll finalization allocates capacity to the organizer first, then Available voters by response time, then Maybe voters by response time; overflow is waitlisted with matching attendance status. Response time is the timestamp of the voter's first submitted poll response and is not changed by later revisions.
- Voter-suggested times become poll options immediately without organizer approval.
- Choosing a poll option maps Available to Going, Maybe to Maybe, and Unavailable to Declined. The organizer is the exception and becomes Going regardless of their vote.
- A poll carries the eventual session's title, platform, optional game, optional notes, minimum attendance, optional maximum capacity, and privacy. Its candidate options vary by start time and by duration, and the selected option's duration becomes the session's. (amended for D-211)
- Finalizing an option that has started but not ended creates an in-progress session. Finalizing one that has ended immediately produces Completed when its mapped Going count meets minimum or Expired otherwise.
- Availability recommendations default to the next seven days, accept a custom date/time range, return all continuous full and partial overlap windows, rank first by the number Available, and use the number Maybe to break ties. Duration is not an input and every selected friend is optional.
- Overlapping availability blocks are merged visually and algorithmically into continuous intervals.
- A recommendation can prefill either a fixed-time session or a poll.
- A fixed-session duration may extend beyond its recommended overlap; the UI warns but allows it.
- The organizer has final judgment over whether a session is viable.
- Sessions can specify a minimum Going count and a maximum capacity.
- Minimum attendance is a required integer of at least 1. Maximum capacity is optional for unlimited attendance; when present, it must be an integer at least as large as minimum attendance.
- The organizer counts toward attendance thresholds only while Going; Going guests count like registered Going participants.
- The organizer starts as Going and may change to Maybe or Declined while remaining the organizer. Ownership transfer is outside the initial release.
- A session becomes Confirmed automatically when its minimum Going count is reached.
- Sessions can have a waitlist.
- Going and Maybe both consume optional maximum capacity; only Going counts toward minimum confirmation attendance.
- At maximum capacity, new Going or Maybe responses enter the waitlist with their selected status. Existing active Maybe participants remain active.
- The organizer manually chooses whom to promote when capacity becomes available.
- Promoting either a Going- or Maybe-waitlisted participant fills the available slot immediately and preserves their selected status.
- Changing a fixed session's start time clears confirmation and returns every participant, including waitlisted participants, to Pending; changing other details does not reset responses.
- After a session starts, its start time and minimum attendance can no longer be edited.
- Recurring sessions are outside the initial release.
- Going and Maybe participants can leave.
- After a previously Confirmed session drops below minimum, it remains Confirmed while at least one Going-waitlisted candidate remains; Maybe-waitlisted candidates do not preserve confirmation. It becomes unconfirmed and notifies everyone only when no Going-waitlisted candidate remains.
- Only the organizer can cancel a session. Cancellation is available while it is unconfirmed, Confirmed, in progress, or Completed. There is no separate Delete action.
- Cancellation is immediate, transitions the session to Canceled, removes it from history, revokes every guest link, and notifies every registered participant who still has access, subject to per-session mute settings.
- The organizer can cancel an unfinished poll at any point before finalization; cancellation is immediate, is not retained in history, and notifies every registered poll participant subject to mute settings.
- A Confirmed session becomes Completed automatically after its end time. An unconfirmed past session becomes Expired. Only non-canceled Completed sessions remain in history; Canceled and Expired sessions do not.
- Completed-session history shows the final RSVP roster. Separate actual-attendance tracking is not supported.
- A session is created either directly at a fixed time or in Polling mode. Polling is optional and never required for direct scheduling.
- Availability and recommendations use 15-minute increments.
- Fixed-time invitation responses close when the session starts.
- Join requests remain open until the session's end time, including while it is in progress and regardless of its Confirmed state.
- Pending responses remain Pending at session start. Going and Maybe participants can continue changing between those attendance states while the session is in progress.
- Once a Confirmed session starts, later attendance changes cannot unconfirm it and it will become Completed. An unconfirmed session may still Confirm until its end time.
- Join requests do not include attendance intent. Approval makes the requester Going, or Going-waitlisted when maximum capacity is full.
- Join requesters can withdraw pending requests. Acceptance and rejection both notify the requester, and an accepted requester can subsequently change attendance normally.
- A **public** session must include at least one directly invited registered friend at creation; a **Groups** session must instead include at least one targeted friend group (D-198). (amended for D-198)
- Reducing maximum capacity below current Going + Maybe occupancy is forbidden. Increasing maximum leaves waitlist promotion manual.
- Changing minimum attendance, allowed only before the session starts, immediately recalculates confirmation and sends the corresponding confirmation/unconfirmation notification. Raising the minimum above the current Going count follows the normal waitlist grace: a Confirmed session remains Confirmed while any Going-waitlisted candidate remains and becomes unconfirmed only when none remains.
- Changing duration sends a rescheduling notification without resetting RSVPs.
- Changes to title, game, platform, notes, maximum capacity, or privacy do not generate notifications.

### 8.2 Games and session details

Fixed sessions and polls collect these eventual session details:

- Required title.
- Start date and time.
- Duration.
- Platform.
- Optional game.
- Optional notes.
- Required minimum attendance.
- Optional maximum player count.

Default values:

- Minimum attendance: 4.
- Maximum capacity: 4.
- Duration: 2 hours.
- Platform: N/A.
- Visibility: **public** (D-198's three-level `Public | Private | Groups`). (amended for D-198)

Duration is entered in 15-minute increments from 15 minutes through 24 hours. The initial release always starts creation with the product defaults above; users cannot save personal defaults.

The initial release supports one platform per session, selected from:

- Steam.
- PlayStation.
- Xbox.
- N/A.

For Steam, PlayStation, or Xbox, the user can choose a game from that platform's full catalog or enter a free-text value. Catalog availability and licensing must be validated for each platform before implementation; free text alone is an acceptable initial fallback wherever a complete reliable catalog is unavailable. For N/A, the game field is free text.

To be decided:

- Game mode, party-size, server/region, lobby, and voice-chat details.
- Ownership or entitlement tracking.
- Catalog synchronization and handling of delisted or duplicate games.

### 8.3 Accounts, friends, and groups

Confirmed:

- The product models existing friends.
- Sessions support direct friend invitations and link invitations.
- Friendship connections are mutual.
- Registration is open to anyone.
- A guest can respond to a link invitation using only a display name.
- Profile links, QR codes, and friend codes initiate friendship. A QR code represents the stable friend code.
- Steam friend-list import is supported initially; other platform imports may follow.
- Arbitrary avatar uploads are not supported. Avatars come from linked platform accounts; unlinked accounts use a selected emoji.
- Registered users can authenticate with email and password, passwordless email, Steam, or Google. Sign in with Apple is added with the mobile release (App Store guideline 4.8 requires it once third-party login ships in an iOS app).
- Email can be used for account recovery.
- Social-only accounts are allowed and are not required to add a separate verified email.
- Users can link multiple authentication methods after registration. The UI does not warn when only one method remains connected.
- Before a friendship is accepted, only the unique username is visible.
- Friend-code rotation is outside the initial release.
- A user cannot unlink their only remaining authentication method; the attempted action returns a clear error. No proactive warning is required.
- Guest-link access is protected by rate limiting, and organizers can revoke individual guest links. Guest-specific blocking and reporting are outside the web MVP.

To be decided:

- Future persistent-group roles and permissions.
- Guest identity reconciliation with a later registered account.
- External platform linking, token storage, unlinking, and import refresh behavior.
- Account verification, credential linking, recovery safeguards, and handling duplicate identities across login methods.

### 8.4 Communication and notifications

Notifications are delivered only to registered users. Guests have no notification channel; they see the current session or poll state, including cancellation, by revisiting their invitation link. Wherever this specification says participants are notified, guests are excluded.

The initial web product will use in-app notifications and Web Push notifications on supported desktop and mobile browsers. iOS Safari supports Web Push only for home-screen-installed PWAs; the product does not pursue that path. iOS Safari users rely on the in-app fallback until the native iOS app ships. After the user completes their first invitation or session action, the app shows its own **Enable notifications** prompt and invokes the browser permission dialog only after the user chooses to enable them. If push is unavailable or denied, the in-app notification center and unread badge are the fallback. Native mobile push notifications are expected when the native apps are built. Email, Discord, WhatsApp, and other channels may be added later.

Push notifications are initially generated for:

- Session invitations.
- Session cancellation.
- Session confirmation.
- Session becoming unconfirmed.
- Session rescheduling.
- Join request received by an organizer.
- Join request accepted or rejected.
- Poll invitation.
- Poll cancellation.
- Voter-suggested poll option.
- Approaching poll deadline.
- Poll closed awaiting organizer selection.
- Poll expired without organizer selection.
- Poll option selected.
- Added to, promoted from, or rejected from a waitlist.
- A waitlist vacancy requiring organizer action.
- A received friend request.
- A Steam-import match.
- One configurable reminder per user per session.

Friend-request acceptance, friend removal, and blocking do not generate notifications.

For poll notifications, a voter-suggested option notifies only the organizer, “deadline approaching” is sent one hour before the deadline, and poll closure, expiry, or cancellation notifies every registered poll participant.

Session comments are not supported.

- Muting a session suppresses every notification, including cancellation, except the configured pre-session reminder when the user is Going or Maybe. Choosing reminder Off suppresses that reminder too.
- The reminder defaults to one hour before the session. Each user can choose Off, 15 minutes, 30 minutes, 1 hour, 3 hours, 12 hours, or 24 hours.

To be decided:

- Unread state, notification grouping, and user preferences.

### 8.5 Calendars and time zones

Each user specifies a time zone. All dates and times are displayed in the viewer's specified time zone. Guests have no specified time zone; guest views display times in the browser-detected time zone. The product does not detect or specially handle travel.

The interface language defaults from the browser or device, falls back to English when unsupported, and can be overridden at the account level. The initial languages are English, Portuguese (Brazil), and Spanish; dates, times, numbers, and week formatting follow the selected locale.

- Store each fixed session, one-time availability block, and poll option as an absolute instant.
- Anchor weekly availability to the owner's specified named time zone and preserve its local wall-clock time across daylight-saving transitions.
- When a user changes their specified time zone, existing weekly availability moves to the same local clock time in the new zone.

Still to be decided:

- Calendar export versus two-way calendar integration.
- Conflict detection.
- Privacy of calendar and availability information.

### 8.6 Trust, safety, privacy, and store compliance

**Policy review date:** 2026-07-28. Store policies must be rechecked before mobile submission.

Session comments are not supported. However, usernames, display names, gaming handles, session titles and notes, and free-text game values are user-authored and visible to other users. Google explicitly defines user-generated content as content visible to even a subset of users, so removing comments does not conclusively remove store moderation obligations.

The web MVP requires blocking. Before mobile-store submission, the product must either implement the applicable UGC controls or remove/constrain user-authored public fields enough to pass a renewed policy review. The conservative mobile-release requirements are:

- Terms of Use and community standards that prohibit objectionable content and behavior.
- In-app reporting of users and relevant user-authored fields.
- The ability to block users.
- Filtering appropriate to the remaining short text fields.
- A moderation workflow with timely review and action.
- Published support/contact information.

The product accepts implementing minimal report-user/report-field and moderation functionality if required by mobile-store review. User-authored text is constrained initially as follows:

- Username: 3–30 characters.
- Display name: 1–50 characters.
- Session title: 1–80 characters.
- Session notes: at most 500 characters.
- Gaming handle: at most 64 characters.
- Free-text game: at most 100 characters.
- Allow Unicode letters and numbers with limited field-appropriate punctuation and apply a profanity filter.

Because the product supports registered accounts, it also requires:

- In-app account deletion.
- A public web route for requesting account and associated-data deletion.
- A privacy policy explaining collection, use, retention, deletion, and third-party data sharing.
- A way to unlink platform accounts and revoke their data access.

Account deletion behavior:

- A deletion request starts a seven-day grace period during which the user can cancel the request.
- Starting deletion immediately signs the account out on every active device and invalidates its login sessions.
- The deletion flow clearly warns that any successful login during the grace period automatically cancels the deletion request and restores normal account use.
- After the grace period, remove the account's friendships and future-session participation.
- After the grace period, show the deleted user as **Anonymous User** in retained Completed-session history.
- After the grace period, cancel future sessions owned by the deleted account.
- After the grace period, expire outstanding polls owned by the deleted account.

The web MVP records only the minimal first-party product events needed to measure confirmation and repeat scheduling. It does not use advertising trackers. No separate analytics opt-in is required by product design, but the privacy policy must accurately describe the collected data and its analytics purpose, and consent or additional notices must be added wherever a launch jurisdiction requires them.

Current official policy references:

- [Apple App Review Guidelines — User-Generated Content and Privacy](https://developer.apple.com/app-store/review/guidelines/)
- [Google Play — User-Generated Content](https://support.google.com/googleplay/android-developer/answer/9876937)
- [Google Play — Account Deletion Requirements](https://support.google.com/googleplay/android-developer/answer/13327111)
- [Google Play — Data Safety](https://support.google.com/googleplay/android-developer/answer/10787469)
- [Google Play — Target Audience and Content](https://support.google.com/googleplay/android-developer/answer/9867159)
- [Apple — App Privacy Details](https://developer.apple.com/app-store/app-privacy-details/)
- [Apple — Declared Age Range API](https://developer.apple.com/documentation/DeclaredAgeRange)
- [Android — Play Age Signals API](https://developer.android.com/google/play/age-signals/use-age-signals-api)
- [Brazil — LGPD](https://www.planalto.gov.br/ccivil_03/_ato2015-2018/2018/lei/l13709compilado.htm)

The stores do not impose one universal minimum user age. The initial product is **13+ and not directed to children**. It must declare an accurate target audience and satisfy the applicable child-privacy rules in every launch region, subject to legal review before launch.

Age gating applies to registered accounts, not guest-link access. Registered native users use the operating system or store-provided age-range signal when available: Apple's Declared Age Range API on iOS and Play Age Signals on Android. When no platform signal is available, including on the web, the user self-attests with an “I am at least 13 years old” checkbox. The service stores only whether the 13+ gate passed and the source of that result; it does not store a birth date. Guests can view and respond through their links without an age gate; this remains a mobile-store/legal review risk.

## 9. Platform scope

### Phase 1: Web

The initial client is a responsive website with Web Push where the browser supports it. Installable PWA behavior is not required. Support targets the current and previous major versions of Chrome, Edge, Firefox, and Safari, including mobile Chrome and Safari.

Registered-user onboarding proceeds in this order:

1. Pass the 13+ age gate.
2. Authenticate or create an account.
3. Choose a unique username.
4. Choose a display name.
5. Choose an emoji avatar or linked-platform avatar.
6. Specify a time zone.
7. Optionally link gaming platforms.
8. Add friends.

The signed-in home screen prioritizes:

1. Pending invitations and join requests.
2. Polls requiring the user's vote.
3. Upcoming sessions.
4. A **Find a time** action and the top five automatic all-friends availability-overlap suggestions of at least 15 minutes, with **See all** for the complete list. Adjacent equivalent windows are not merged into a single suggestion in the initial release.

Discoverable non-private sessions appear on Home, in the calendar/agenda views, and in a dedicated **Discover** view.

Scheduling is available through both a week-calendar view and a chronological agenda view. The calendar shows all friends' availability by default and can combine personal availability, fixed sessions, and poll options. Filters and layer toggles control clutter and reset to defaults on each visit.

Unfinished session and poll forms are not saved as drafts. After the user changes a creation form, navigating away or closing it requires confirmation before the entered data is discarded.

### Future: Android and iOS

The product and API should not unnecessarily prevent native mobile clients. Whether the mobile clients will be native, cross-platform, or packaged web applications is intentionally undecided.

## 10. Business model and administration

The initial product will be publicly available, open for direct registration, and free to use. Invitations are also supported.

Administration (confirmed):

- The product has an admin role assigned operationally (configuration/seed at deploy time); there is no in-app role management.
- Admins see a usage dashboard: registrations, active users, sessions and polls created, confirmation rate, and 30-day repeat scheduling — the section 12 measures.
- Admins see a user list with per-user stats (registered date, last activity, sessions organized and joined, friend count) and ban state.
- Admins can ban and unban users. A ban immediately signs the account out on every device, blocks login, and hides the user from lookup and discovery; the user's content and history are otherwise untouched, and unban fully restores access. When banning, the admin may additionally choose to remove the user's content — cancel future owned sessions, expire owned polls, and remove future participation (the account-deletion effects without the tombstone); content removal is not reversed by a later unban. A banned user can still request account deletion through the public route.

To be decided:

- Whether monetization may be introduced later.
- Staffing and service-level expectations for required content/user reports.
- Detailed moderation administration tools.
- Data-retention periods and deletion/anonymization behavior for shared session content.

## 11. Quality attributes

Confirmed targets and areas still requiring detailed targets include:

- Accessibility: [WCAG 2.2](https://www.w3.org/TR/WCAG22/) Level AA across the complete responsive web experience, with keyboard access, visible focus, screen-reader semantics/status announcements, sufficient contrast and target sizes, non-color-only communication, reduced-motion support, and non-drag alternatives.
- Privacy and security.
- Performance.
- Availability and reliability.
- Localization. The initial UI supports English, Portuguese (Brazil), and Spanish, defaults from the browser/device with English fallback, allows an account override, and uses locale-appropriate dates, times, numbers, and week formatting.
- Supported browsers and devices.
- Expected scale.

## 12. Success criteria

The two primary launch measures are:

- The percentage of created fixed sessions and polls that result in a Confirmed session, with a provisional target of at least 50%.
- Repeat scheduling within 30 days, with a provisional target of at least 50%.

The confirmation denominator includes every created fixed session and poll, including later cancellations. A poll counts as one proposal and succeeds only when its resulting fixed session becomes Confirmed. Repeat scheduling is the percentage of registered organizers who create another fixed session or poll within 30 days of their first. Time to confirmation and participant response rate are useful secondary diagnostics; actual-attendance rate cannot be measured because the initial release records only the final RSVP roster.

## 13. Delivery boundaries

Every feature labeled **initial release**, **initial web product**, or **web MVP** in this specification is required for the first public launch. There will not be a reduced public alpha or beta feature set. Implementation and private testing may still proceed incrementally, but public v1 launches as one complete scope. Additional ideas remain available for later releases.

## 14. Decisions

| ID | Decision | Status |
|---|---|---|
| D-001 | The product coordinates multiplayer game sessions among friends. | Confirmed |
| D-002 | The web interface is the first delivery target. | Confirmed |
| D-003 | Android and iOS support is planned for later. | Confirmed |
| D-004 | The product serves existing friends; stranger discovery and public matchmaking are out of scope. | Confirmed |
| D-005 | An organizer can create a session for a fixed time and invite friends. | Confirmed |
| D-006 | Invitees can respond Going, Maybe, or Declined. | Confirmed |
| D-007 | A **public** session lets eligible non-invited friends see it and request to join via friend-of-participant inheritance (rule 2); a **Groups** session instead grants that same see-and-request-to-join access to a targeted friend group's Accepted members and owner (rule 2g, D-198), with no further inheritance; a private session grants neither. (amended for D-198) | Confirmed |
| D-008 | Session viability is ultimately the organizer's judgment. | Confirmed |
| D-009 | Users can publish one-time and recurring availability blocks for friends to see. | Confirmed |
| D-010 | A session's game is optional. | Confirmed |
| D-011 | Session details include platform, duration, and notes. | Confirmed |
| D-012 | Invitations can be shared by link. | Confirmed |
| D-013 | Initial notifications use in-app and supported browser/mobile Web Push; other external channels may come later. | Confirmed |
| D-014 | The initial product is publicly available and free. | Confirmed |
| D-015 | Friendship is a mutual connection accepted by both people. | Confirmed |
| D-016 | Anyone can register directly; invitations are also supported. | Confirmed |
| D-017 | A link invitee can respond as a guest using only a display name. | Confirmed |
| D-018 | The organizer, pending invitees, approved participants, and waitlisted users extend a **public** session's visibility; unapproved requesters, Declined users, and Rejected users do not. Groups-level sessions do not extend via this inheritance chain at all — see D-198's rule 2g. (amended for D-198) | Confirmed |
| D-019 | An organizer can make a session private; it is visible only to invitees and any retained participants or requesters. | Confirmed |
| D-020 | Join requests require organizer approval, and invitations can be added after session creation. | Confirmed |
| D-021 | Sessions can have a maximum player count and a waitlist. | Confirmed |
| D-023 | The product calculates availability overlaps and recommends possible times. | Confirmed |
| D-024 | Initial availability uses exact time blocks visible to all friends. | Confirmed |
| D-025 | Recurring availability is weekly and supports removing one occurrence or the entire series. | Confirmed |
| D-026 | Organizers can poll participants about multiple candidate times. | Confirmed |
| D-027 | Changing a session's start time resets responses; other detail changes do not. | Confirmed |
| D-028 | Sessions support comments. | Superseded by D-073 |
| D-030 | Anyone who can see a session can see its attendee list and individual responses. | Confirmed |
| D-031 | Users can connect through profile links, QR codes, friend codes, and Steam friend-list import. | Confirmed |
| D-032 | Profiles contain display name, unique username, avatar, and gaming handles/platforms; arbitrary image uploads are forbidden. | Confirmed |
| D-033 | Poll choices are Available, Maybe, and Unavailable; guests can vote and voters can suggest times. | Confirmed |
| D-034 | Selecting a poll option automatically makes its Available voters Going. | Confirmed |
| D-035 | Availability recommendations rank times by the number of available selected friends; all selected friends are optional. | Confirmed |
| D-036 | Recurring sessions and persistent named groups are outside the initial release. | Confirmed |
| D-037 | Maybe does not reserve capacity; full capacity moves Maybe participants to the waitlist, and the organizer chooses promotions. | Superseded by D-084 |
| D-038 | Dates and times are displayed in the viewer's preferred time zone. | Confirmed |
| D-039 | The first client is a responsive website with Web Push; installable PWA behavior is not required. | Confirmed |
| D-040 | The initial platform list is Steam, PlayStation, Xbox, and N/A, with one platform per session. | Confirmed |
| D-041 | A platform game can come from its catalog or free text, subject to catalog feasibility. | Confirmed |
| D-042 | Initial pushes cover invitations, confirmation, unconfirmation, rescheduling, and a configurable reminder. | Superseded by the §8.4 notification list |
| D-043 | Making a session private preserves existing non-invited participants and outstanding join requests. | Confirmed |
| D-044 | Guests can edit through the same link; the organizer can remove pending or confirmed guests and revoke their access. | Confirmed |
| D-045 | Anyone who can see a session can read and write its comments, including guests. | Superseded by D-073 |
| D-046 | Each user specifies a time zone; all times display in the viewer's specified zone and travel is not specially handled. | Confirmed |
| D-047 | When a session becomes private, existing participants and requests remain but viewers who only inherited visibility lose access. | Confirmed |
| D-048 | Each guest has a unique link that expires after the session and cannot be renewed. | Confirmed |
| D-049 | Steam import suggests matched users for mutual friendship and offers invitation links for unmatched friends. | Confirmed |
| D-050 | Users choose among linked-platform avatars; unlinked users can select an emoji avatar. | Confirmed |
| D-051 | Poll suggestions appear without approval; finalization maps Available to Going, Maybe to Maybe, and Unavailable to Declined. | Confirmed |
| D-052 | Availability ranking uses Available count first and Maybe count as its tie-breaker. | Confirmed |
| D-053 | Sessions require minimum attendance, may specify maximum capacity, and become Confirmed when the minimum Going count is reached. | Confirmed |
| D-054 | Comment pushes go to every associated user or guest except Declined and Rejected people. | Superseded by D-073 |
| D-055 | Going and Maybe participants can leave a session. | Confirmed |
| D-056 | Only organizers can cancel; Confirmed sessions complete after their end time, unconfirmed past sessions expire, and only non-canceled Completed sessions remain in history. (amended for D-221: Canceled sessions the viewer organized or was Going in now also appear in history, marked distinct; merely-visible sessions the viewer never joined no longer appear.) | Confirmed |
| D-057 | Every session requires a title. | Confirmed |
| D-058 | N/A uses a free-text game; free text is an acceptable fallback when a reliable platform catalog is unavailable. | Confirmed |
| D-059 | Blocking is required; mobile UGC controls remain a release gate for the remaining user-authored fields. | Policy-required |
| D-060 | Initial localization supports English, Portuguese (Brazil), and Spanish with locale-aware date and time formatting. | Confirmed |
| D-061 | Changing the user's specified time zone moves weekly availability to the same local clock time in the new zone. | Confirmed |
| D-062 | A Going organizer and Going guests count toward minimum; Going and Maybe count toward optional maximum, which cannot be below minimum. | Confirmed |
| D-063 | A start-time change clears confirmation and returns every participant, including waitlisted participants, to Pending. | Confirmed |
| D-064 | Polling is an optional creation mode; organizers can instead schedule a fixed-time session directly. | Confirmed |
| D-065 | Blocking is mutually invisible and removes friendship; it does not generally remove users from shared sessions. | Confirmed |
| D-066 | Authors can edit/delete their comments and session organizers can delete any session comment. | Superseded by D-073 |
| D-067 | Initial authentication supports password, passwordless email, Steam, and Google; email supports recovery. Sign in with Apple is added with the mobile release (App Store guideline 4.8 requires it once third-party login ships in an iOS app). | Confirmed |
| D-068 | Before friendship approval, a profile exposes only its unique username; friend-code rotation is outside MVP. | Confirmed |
| D-069 | Users can mute notifications per session and customize the default one-hour reminder. | Confirmed |
| D-070 | Poll organizers choose a 6-, 12-, 24-, or 48-hour voting deadline. | Confirmed |
| D-071 | Availability entry and recommendations use 15-minute increments. | Confirmed |
| D-072 | Account deletion is available in-app and through a public web request route. | Policy-required |
| D-073 | Session comments are not supported. | Confirmed |
| D-074 | A previously Confirmed session remains Confirmed while any Going-waitlisted candidate remains and becomes unconfirmed when none remains; Maybe-waitlisted candidates do not preserve confirmation. | Confirmed |
| D-075 | The organizer begins Going, may change attendance, and cannot transfer ownership. | Confirmed |
| D-076 | Blocked users retain third-party shared-session access but see one another as Anonymous User. | Confirmed |
| D-077 | Blocking between an organizer and participant removes the participant from that organizer's sessions in either direction. | Confirmed |
| D-078 | Social-only accounts do not require a separate verified recovery email. | Confirmed |
| D-079 | Poll voting closes at its deadline and waits for organizer selection. **Amended by D-187:** selection is no longer gated behind the deadline once an option is already viable. | Confirmed |
| D-080 | Polls allow 12 distinct options, deduplicate identical times, and let organizers delete any option. | Confirmed |
| D-081 | Fixed-session responses close at the session start. | Confirmed |
| D-082 | Each user gets one reminder chosen from Off, 15m, 30m, 1h, 3h, 12h, or 24h; default is 1h. | Confirmed |
| D-083 | The initial audience is 13+ and not directed to children. | Confirmed |
| D-084 | Going and Maybe both consume maximum capacity; at capacity, new Going or Maybe responses are waitlisted without displacing active Maybes. | Confirmed |
| D-085 | The organizer continues organizing and extending a **public** session's visibility while Maybe or Declined. Groups sessions instead extend access only through rule 2g's group membership (D-198), independent of the organizer's own attendance. (amended for D-198) | Confirmed |
| D-086 | Anonymous User retains normal attendance and capacity effects; anonymization applies only between the blocked pair. | Confirmed |
| D-087 | Users can link more authentication methods later; no warning is required when only one remains. | Confirmed |
| D-088 | Closed polls expire after 48 hours without organizer selection; deleting a poll option does not notify voters. | Confirmed |
| D-089 | Pending remains Pending at session start; Going and Maybe participants may change attendance during the session. | Confirmed |
| D-090 | Muting suppresses all session pushes except the separately configured reminder for Going or Maybe users. | Confirmed |
| D-091 | Sessions without maximum capacity have unlimited Going and Maybe attendance and no waitlist. | Confirmed |
| D-092 | Account deletion removes future participation and friendships, anonymizes Completed history, cancels owned future sessions, and expires owned polls. | Confirmed |
| D-093 | Native clients use OS/store age signals where available; fallback is 13+ self-attestation, and no birth date is retained. | Confirmed |
| D-094 | Remaining free-text fields may be constrained as necessary for mobile-store compliance. | Confirmed |
| D-095 | Completed history uses the final RSVP roster; separate actual-attendance tracking is not supported. | Confirmed |
| D-096 | User-authored text uses agreed length limits, limited punctuation, Unicode letters/numbers, and profanity filtering. | Confirmed |
| D-097 | Mobile release may add minimal report-user/report-field and moderation functionality if store review requires it. | Confirmed |
| D-098 | Join requests carry no attendance choice; approval makes the requester Going or Going-waitlisted when full. | Confirmed |
| D-099 | A **public** session requires at least one directly invited registered friend when created; a **Groups** session requires at least one targeted friend group at creation (direct invitees optional). D-198's visibility levels define the terms. (amended for D-198) | Confirmed |
| D-100 | Availability matching uses selected friends and a date/time range, not desired duration. | Confirmed |
| D-101 | Overlapping availability blocks merge visually and algorithmically into continuous intervals. | Confirmed |
| D-102 | Recommendations can prefill either a fixed session or a poll. | Confirmed |
| D-103 | Home prioritizes actionable invitations/requests and polls, then upcoming sessions and availability suggestions. | Confirmed |
| D-104 | The web client provides both week-calendar and chronological-agenda scheduling views. | Confirmed |
| D-105 | Friend pushes are sent for received requests and Steam-import matches, not acceptance, removal, or blocking. | Confirmed |
| D-106 | A Confirmed in-progress session cannot unconfirm and will Complete; an unconfirmed session may Confirm until its end. | Confirmed |
| D-107 | Join requesters can withdraw; acceptance/rejection notifies them, and accepted users may change attendance normally. | Confirmed |
| D-108 | Recommendations default to seven days and return all ranked full and partial overlaps without an artificial result limit. | Confirmed |
| D-109 | A fixed-session duration may exceed its recommended overlap with a warning. | Confirmed |
| D-110 | Home offers both Find a time and automatic all-friends suggestions. | Confirmed |
| D-111 | Calendar and agenda views layer personal/friend availability, sessions, and poll options with toggles. | Confirmed |
| D-112 | All specified join-request, poll, and waitlist events generate pushes. | Confirmed |
| D-113 | Muting suppresses cancellation and every other session push except a separately enabled Going/Maybe reminder. | Confirmed |
| D-114 | Anyone who discovers a session can see its notes, complete roster, and responses before requesting to join. | Confirmed |
| D-115 | Private sessions support transferable unique guest links; possession controls that guest identity. | Confirmed |
| D-116 | Home shows five automatic all-friends suggestions with See all for the complete list. | Confirmed |
| D-117 | Calendar shows all friends' availability by default and offers filters to reduce clutter. | Confirmed |
| D-118 | A **public** poll inherits normal (rule 2) visibility and accepts join requests during Polling; a **Groups** poll instead grants that same request-to-join access to a targeted friend group's Accepted members and owner (rule 2g, D-198). (amended for D-198) | Confirmed |
| D-119 | Poll finalization allocates organizer first, Available by response time, then Maybe by response time; overflow is waitlisted. | Confirmed |
| D-120 | Suggested poll options notify only the organizer; poll closure notifies everyone; deadline warning is one hour before. | Confirmed |
| D-121 | Maximum capacity cannot be reduced below active Going + Maybe occupancy. | Confirmed |
| D-122 | Minimum changes recalculate confirmation immediately, with raises subject to the D-074 Going-waitlist grace; duration changes send rescheduling notifications without resetting RSVPs. | Confirmed |
| D-123 | Title, game, platform, notes, maximum, and privacy changes do not notify participants. | Confirmed |
| D-124 | Guest-link access does not require the 13+ age gate. | Confirmed with policy risk |
| D-125 | Private sessions may be created with guest links only; a **public** poll requires an invited registered friend, and a **Groups** poll instead requires at least one targeted friend group at creation (D-198). (amended for D-198) | Confirmed |
| D-126 | Multi-device guest-link edits use last-write-wins until revocation or expiry. | Confirmed |
| D-127 | Onboarding is age gate, authentication, username, display name, avatar, time zone, optional platform linking, then friends. | Confirmed |
| D-128 | Usernames are case-insensitive and changeable once every 30 days; old profile links stop while stable friend codes persist. | Confirmed |
| D-129 | Fixed-session defaults are minimum 4, maximum 4, two hours, N/A platform, and **public** visibility (D-198's three-level `Public \| Private \| Groups`). (amended for D-198) | Confirmed |
| D-130 | Sessions start at least 15 minutes and at most one year in the future. | Confirmed |
| D-131 | Polls default to 24 hours, require two initial options, and may contain options before the deadline. Each initial option also carries its own duration (D-211). | Confirmed |
| D-132 | Availability is selected on a week calendar in 15-minute increments with one-time or weekly recurrence. | Confirmed |
| D-133 | Weekly availability continues until deleted. | Confirmed |
| D-134 | Recurring-occurrence edits expose a checkbox to apply the change to one occurrence or the series. | Confirmed |
| D-135 | Calendar filters reset to defaults on every visit. | Confirmed |
| D-136 | Home suggestions require 15 minutes of overlap and do not merge adjacent equivalent windows initially. | Confirmed |
| D-137 | The only remaining authentication method cannot be unlinked and returns an explicit error. | Confirmed |
| D-138 | Web support targets current and previous Chrome, Edge, Firefox, and Safari, including mobile variants. | Confirmed |
| D-139 | The responsive web release targets WCAG 2.2 Level AA. | Confirmed |
| D-140 | Elapsed poll options remain visible and selectable, and a poll does not expire early merely because all options have passed. | Confirmed |
| D-141 | Minimum attendance is an integer of at least 1; maximum is blank for unlimited or an integer at least equal to minimum. | Confirmed |
| D-142 | Sessions last 15 minutes to 24 hours in 15-minute increments. The same bounds apply to each poll candidate option's own duration (D-211). | Confirmed |
| D-143 | Recurring-availability edits default to one occurrence; an explicit checkbox applies the edit to that occurrence and all future occurrences. | Confirmed |
| D-144 | QR codes use the stable friend code rather than a username-based profile link. | Confirmed |
| D-145 | A former username is reserved for 30 days after a change. | Confirmed |
| D-146 | Language defaults from the browser/device, falls back to English, and supports an account override. | Confirmed |
| D-147 | The product asks about Web Push after the first invitation/session action; in-app notifications and an unread badge are the fallback. | Confirmed |
| D-148 | Account deletion has a cancelable seven-day grace period before destructive effects occur. | Confirmed |
| D-149 | Discoverable sessions appear on Home, calendar/agenda, and a dedicated Discover view. | Confirmed |
| D-150 | Web-MVP guest abuse controls are link rate limiting and organizer revocation; guest blocking/reporting is deferred. | Confirmed |
| D-151 | Primary launch measures are confirmation and 30-day repeat scheduling, each with a provisional 50% target. | Confirmed |
| D-152 | Users cannot save custom session-creation defaults in the initial release. | Confirmed |
| D-153 | Finalizing a started poll option creates an in-progress session; finalizing an ended option immediately produces Completed or Expired according to its mapped Going count. | Confirmed |
| D-154 | Polls collect all eventual session details up front, and candidate options vary only by start time. **Amended by D-211:** options also vary by duration, and the poll itself no longer carries one. | Confirmed |
| D-155 | Organizers vote on poll options like other voters but become Going when an option is finalized. | Confirmed |
| D-156 | Organizers can cancel unconfirmed, Confirmed, in-progress, or Completed sessions and can cancel polls before finalization. | Confirmed |
| D-157 | “Delete a previously Confirmed session” means Cancel; there is no separate session Delete action. | Clarified |
| D-158 | Any successful login during the seven-day account-deletion grace period cancels the deletion request, as disclosed when deletion begins. | Confirmed |
| D-159 | The app presents a contextual enable-notifications prompt before invoking the browser's Web Push permission dialog. | Confirmed |
| D-160 | Confirmation includes all created proposals in its denominator; repeat scheduling measures organizers who create again within 30 days. | Confirmed |
| D-161 | MVP analytics use minimal first-party events and no advertising trackers; required privacy transparency still applies. | Confirmed with policy constraint |
| D-162 | Creation forms have no drafts and warn before discarding edited data. | Confirmed |
| D-163 | All initial-release features ship together in the first public web release; reduced public alpha/beta scopes are rejected. | Confirmed |
| D-164 | Expired sessions, including Expired results from late poll finalization, are never retained in history. | Confirmed |
| D-165 | Starting account deletion invalidates all active login sessions; a successful login during the grace period cancels deletion. | Confirmed |
| D-166 | Canceling any session, including a Completed session, notifies its remaining registered participants; canceling a poll notifies every registered poll participant. | Confirmed |
| D-167 | Guests receive no notifications through any channel; they see current state only by revisiting their link. | Confirmed |
| D-168 | Start time and minimum attendance cannot be edited after the session starts. | Confirmed |
| D-169 | Guest views display times in the browser-detected time zone. | Confirmed |
| D-170 | Poll expiry without organizer selection notifies every registered poll participant. | Confirmed |
| D-171 | Suggesting a poll option beyond the 12-option limit fails with a clear error. | Confirmed |
| D-172 | Poll-finalization response time is the voter's first-response timestamp and is unchanged by vote revisions. | Confirmed |
| D-173 | Join requests remain open until the session's end time, including while it is in progress. | Confirmed |
| D-174 | Canceling a session immediately revokes all of its guest links. | Confirmed |
| D-175 | iOS Safari Web Push (home-screen PWA requirement) is not pursued; iOS web users get the in-app fallback until the native app. | Confirmed |
| D-176 | PlayStation and Xbox game fields are free text at launch (no viable public catalog APIs); only Steam offers catalog selection, per the D-058 fallback. Catalog selection is implemented per D-194's cache. | Confirmed |
| D-177 | An admin role exists, assigned operationally at deploy time; no in-app role management. | Confirmed |
| D-178 | Admins see a usage dashboard covering registrations, active users, sessions/polls created, confirmation rate, and 30-day repeat scheduling. | Confirmed |
| D-179 | Admins see a user list with per-user stats and ban state. | Confirmed |
| D-180 | Admins can ban/unban. Ban = sign-out everywhere + login blocked + hidden from lookup/discovery, content untouched, unban restores; the admin may optionally also remove the user's content (the D-092 effects without the tombstone), which unban does not reverse. | Confirmed |
| D-181 | The app offers a theme preference — system (default), light, dark — set in settings, stored client-side only (localStorage); no server or API involvement. | Confirmed |
| D-182 | After onboarding, a registered account's first visit to the app home starts a skippable 9-stop tour of the app chrome. Finishing or skipping marks the tour completed for the account (any device); it never auto-shows again. The tour is replayable from account settings. Accounts that had completed onboarding before the feature ships are treated as completed. | Confirmed |
| D-183 | The app chrome ends in an account menu: the signed-in account's D-050 avatar acts as the trigger in the top-right, opening a menu that holds the settings entry and a sign-out action. Sign out ends the session on the current device only — other devices stay signed in — and returns to the welcome screen. The trigger is the anchor for the tour's settings stop (D-182 keeps its nine stops). | Confirmed |
| D-184 | Password strength is governed by length alone — a 12-character minimum, with no composition rules (no required digit, symbol, or mixed case). This is deliberate, per NIST SP 800-63B: composition rules push users toward predictable substitutions without materially raising entropy. Form validation is rendered by the app and localized; the browser's native validation UI is never the mechanism, because it is unlocalized and unstyled. | Confirmed |
| D-185 | Suggesting a poll option also records the suggester's own vote as **Available** on it — suggesting a time asserts availability at that time. This applies to guests exactly as to registered participants (D-033). The vote is a real vote: it counts in tallies and stamps the suggester's D-172 first-response time. When the suggestion deduplicates onto an existing option (D-080), the auto-vote **overwrites** any vote the suggester already cast there, including an explicit Unavailable — the later suggestion is treated as the newer statement of availability. | Confirmed |
| D-186 | A registered poll participant can remove themselves from a poll. Leaving withdraws their votes — the votes are gone, not merely ignored at finalization — and removes their participation record entirely, so the participation is undone rather than frozen; nothing of it, including their first-response time, survives. Leaving is silent: the organizer is not notified (D-088), and sees only the roster shrink. Someone who left may rejoin by the same route they first joined (a fresh invitation acceptance or join request); leaving is not a ban. If they rejoin and vote again, their first-response time is set fresh by that new vote — the D-172 write-once rule applies within a participation, not across a leave. The organizer is the exception: they hold the poll's own participant row and appear on the finalized roster unconditionally (D-155), so they cannot leave their own poll — they cancel it instead (D-156). Guests are unchanged: their removal stays organizer-driven. | Confirmed |
| D-187 | An organizer may finalize a poll **before its deadline** as soon as at least one option has enough **Available** votes to meet the session's minimum attendance. It is manual: the organizer decides whether and which option to select, and nothing converts automatically. Only Available votes count toward the threshold — Maybe maps to Maybe rather than Going (D-051), so counting it could convert a session that is short on actual attendance, which is what minimum attendance exists to prevent. In every other respect this is the ordinary finalization transition: the poll becomes Finalized, votes map per D-051, overflow waitlists in D-119 order, guests re-parent, and the created session's status follows D-153. Amends D-079. | Confirmed |
| D-188 | The D-050 avatar renders wherever a person is listed: session rosters and waitlists, poll voter lists and the finalization preview, notification rows, the blocked-users list, and Steam import matches — alongside the friend list, profile, account menu and friend selectors that already show it. It is decoration: it never replaces the name or becomes a control's accessible name. **Two exclusions.** Friend request cards stay username-only, preserving the shipped contrast that an accepted friend is avatar-forward and a pending request is not. Masked participants keep the neutral treatment, and the server withholds the avatar exactly as it withholds the display name — emitting a real avatar for a masked participant would leak a blocked user's identity through the disc (D-076). | Confirmed |
| D-189 | Registered users receive email for three events: a direct session invite, a direct poll invite, and a join request being approved. One account setting governs all of it, defaults **on**, and is email-only — in-app notifications are unaffected. Guests never receive it (they receive no notifications through any channel). Mail renders in the recipient's locale (D-060/D-146) and dispatches **post-commit** as an idempotent transport, never inside the state change's transaction (A-016). The body carries the full details including notes, accepting that this content leaves the visibility engine permanently: a later privacy flip (D-047) or removal cannot recall what was already delivered. Every email carries an unsubscribe URL bearing a token per delivered email; redeeming one consumes every outstanding unsubscribe token for the account — CSPRNG, stored hashed, never logged (A-015) — that opens a confirmation page rather than mutating on GET, because mail scanners fetch every link in a message. Acting on it disables the setting and rotates the token, so a forwarded or cached link is dead; the link can only ever disable, never re-enable, which requires signing in. All user-authored content in an email — titles, notes, display names — is escaped for the context it lands in, and subject lines reject CR/LF. | Confirmed |
| D-190 | Avatar sources include picture-bearing **auth** accounts (Google), not only gaming platform links. D-050's "linked platform account" reads "linked account". | Confirmed |
| D-191 | A PartyUp user's avatar picture is served only through the first-party proxy, from bytes re-encoded server-side at capture. Upstream URLs are never emitted to clients. **Carve-out:** `SteamUnmatchedFriendResponse.AvatarUrl` (`FriendEndpoints.cs:709`) keeps its raw steamstatic URL — those people have no `User` row and therefore no avatar source. | Confirmed |
| D-192 | The blocked-users list renders a neutral disc seeded from the stored username snapshot and carries no avatar. Amends D-188, which listed it as an avatar surface. | Confirmed |
| D-193 | The organizer may invite registered friends to a poll after creation, while the poll is **Polling** — invitations close the moment the poll leaves Polling (Closed, Finalized, Expired, or Canceled), because a post-deadline invitee could never vote. Mid-flight invitees get exactly the creation-time treatment: they become Active participants immediately with no votes (D-033), receive the D-112 in-app notification and the D-189 direct-poll-invite email, and gain visibility through participation. Only the organizer invites; invitees must be the organizer's friends (D-020 posture), blocked pairs cannot be invited (D-065/D-077), and a Removed participant is never re-invited over (removal is permanent, as in sessions §5.2). D-119 finalization ordering is unchanged: a late invitee's response time is their first vote, so arriving late costs allocation priority and nothing else. | Confirmed |
| D-194 | Steam catalog selection is served from a first-party cache of `IStoreService/GetAppList` (games only), refreshed every 6 hours incrementally via `if_modified_since`; search is fuzzy; free text remains valid alongside (D-058); when the catalog is unavailable the UI degrades to free text plus the optional manual App ID (D-176). | Confirmed |
| D-195 | Per-option poll votes (who voted Available/Maybe/Unavailable, ordered by first response time) are visible to everyone who can read the poll, guest-link holders included. Voter identities carry the response's standard masking: a blocked-pair counterpart renders as Anonymous User with no avatar in the option lists exactly as in the roster (D-076), and the registered arm and guest arm expose the same lists their existing read paths already serve. | Confirmed |
| D-196 | A suggested poll option displays who suggested it, as a small attribution visible to everyone who can read the poll. The suggester identity carries the same masking as D-195's voter lists (blocked-pair counterparts render as Anonymous User; guest suggesters render under their guest display name). Organizer-created options carry no attribution. | Confirmed |
| D-197 | When a signed-in user's browser time zone differs from their profile time zone, the app prompts once — a dismissible dialog offering to update the profile zone to the detected one. Dismissal persists per zone pair in the browser (D-181 idiom): the same mismatch never re-prompts, a new mismatch does. Declining changes nothing; accepting updates the profile zone through the normal settings path. | Confirmed |
| D-198 | Session/poll visibility gains a third level: `Public \| Private \| Groups`. A **Groups** target admits access only through rule 2g — the viewer is the owner or an **Accepted** member of ≥1 of the target's targeted friend groups; `Invited` membership grants nothing. Groups visibility is strict: there is no friend-of-extender inheritance on a Groups target the way rule 2 gives Public targets. The canonical visibility-rule evaluation order is **4 → 1 → level gate → inheritance** (rule 4 blocking, then rule 1 direct access, then the Public/Private/Groups level gate, then inheritance — rule 2g for Groups, rule 2 for Public). See DOMAIN_MODEL §7. | Confirmed |
| D-199 | A **FriendGroup** is a new shared, owner-managed aggregate: `Name` 1–50 characters trimmed, unique per owner case-insensitive; only the owner can rename, delete, invite, revoke, or remove; members see the group's name and roster. Deleting a group is a hard delete cascading its membership rows and every session/poll audience junction row that targeted it (sessions/polls that targeted it live on with a shrunken audience, per D-205). Group reads are fail-closed: a non-member gets a 404 indistinguishable from nonexistent. An `Invited` viewer reads name + owner only, enough to answer the invite; an **Accepted** member or the owner reads the full roster, which carries D-076-style blocked-pair masking. | Confirmed |
| D-200 | Friend-group membership is invite/accept consented, same row model as Friendship (no tombstones): decline, revoke, leave, and remove all delete the membership row outright. An invite requires an Accepted friendship between owner and invitee **at invite time only**, checked best-effort without cross-aggregate locking; membership then **survives unfriending** — friendship is required only at the moment of invite, never re-validated afterward. | Confirmed |
| D-201 | A session or poll can target **1 to n** friend groups; the audience is their membership union. A **Groups**-level session/poll requires ≥1 targeted group at creation. Any owner or Accepted member of a group may target it on their own sessions/polls. Targeting authority is re-checked only for a **newly added** group at creation or edit time — an already-targeted group is never re-validated later, so an organizer who has left group G can still edit other fields or add group H without being forced to drop G. | Confirmed |
| D-202 | A block between a friend-group **owner and a member** (either direction) deletes that membership row — both `Invited` and `Accepted` — silently, in the same transaction as the friendship deletion (mirrors D-077's organizer↔participant removal, with the group owner as the organizer-analog). A block between two **non-owner members** leaves both rows in place; their coexistence is masked at render time (a D-076 extension). The group-invite command returns the identical "not a friend" error whether the target is a stranger or a blocked user — a distinguishable error would be a block-detection oracle. | Confirmed |
| D-203 | DOMAIN_MODEL §5.6's "becomes private" retention rule generalizes to **"audience narrows"**: Public→Private, Public→Groups, Groups→Private, or removing a targeted group from a Groups audience all retain the existing rule-1 set (D-043/D-047 posture) and are silent (D-123). Any edit that leaves the Groups level deletes the audience junction rows outright — returning to Groups later means re-picking, never a silent resurrection of stale or no-longer-authorized targeting. | Confirmed |
| D-204 | Poll finalization carries the poll's visibility level onto the created session exactly as it carries every other detail (D-154), and copies the poll's audience junction rows onto the session in the same finalization transaction. | Confirmed |
| D-205 | A targeted friend group's deletion can leave a Groups-level session/poll with an empty audience. This zero-group state is a legal degenerate state: the target degrades to rule-1-only (effectively private) visibility, fail-closed; the ≥1-group requirement (D-201) is creation/edit-time only, not a standing invariant, mirroring D-099's own creation-time-only posture. Affected organizers receive an in-app notification that a targeted group was deleted. | Confirmed |
| D-206 | A session/poll card shows the granting friend group's name — a "via `<group>`" label — only to viewers who are that group's owner or a member, rendered from the live group row so a later rename stays accurate. Every other viewer with access to a Groups target (direct invitees, guest-link holders, pending requesters, members of only a different targeted group) instead sees a neutral "shared with a group" label. Group names are never serialized to non-members; this is presentation on top of real access, not a second visibility channel (the D-196 posture). | Confirmed |
| D-207 | In-app notifications for friend groups, committed transactionally (A-016): a group invite received (→ invitee), an invite accepted (→ owner), and a targeted group's deletion (→ each affected organizer). Silent: decline, revoke, remove, leave, and block-driven membership removal — the same D-105 push posture that keeps friend-removal and blocking non-punitive. An invite notification referencing a since-deleted or since-revoked group renders masked — a dangling-ref rule parallel to tombstone masking — and never 404s the notification list. | Confirmed |
| D-208 | Friend-group caps, app-checked under lock: 50 groups owned per user (creation locks the owner's `users` row before counting) and 100 membership rows per group, counting both `Invited` and `Accepted` (invite/accept lock the group row before counting, the session-RSVP idiom). The owner is never counted toward the 100-row cap — they are an implicit member with no row. | Confirmed |
| D-209 | Account deletion execution (§11) and D-180's admin content removal hard-delete every friend group the subject owns (full cascade, D-199) and every membership row — `Invited` and `Accepted` — the subject holds in others' groups, in the same executor pass as the D-092/D-180 effects they already run. | Confirmed |
| D-210 | Because friend-group membership survives unfriending (D-200), the unfriend flow shows the acting user, and only them, which of their groups the unfriended person remains in, with an optional one-tap "also remove from my groups" action. Backend unfriend behavior is unchanged; this is actor-side presentation only. | Confirmed |
| D-211 | Each poll candidate option carries its **own duration**, bounded by D-142 (15 minutes to 24 hours, 15-minute steps) per option; the poll itself carries no duration. Finalization gives the created session the winning option's duration, and an option's end time stays derived from its start plus its own duration, never stored. Deduplication is unchanged and still keyed on the start instant alone (D-080): an identical instant merges into the existing option and keeps the duration already recorded there. (ratified 2026-08-17) | Confirmed |
| D-212 | An in-app feedback channel accepts a **type** (`Bug \| Idea \| Other`) and a required free-text **message**, capped at 4000 characters. Any user or guest/anonymous visitor may submit; there is no minimum-relationship or friendship gate. The client auto-attaches current route, user agent, UI locale, and app version as context; every attached field is optional and nullable server-side, so a submission with none of them still succeeds. (ratified 2026-08-18) | Confirmed |
| D-213 | `POST /api/v1/feedback` allows unauthenticated callers; an authenticated caller's user id is recorded on the row. Because the endpoint is reachable with no account, it gets a dedicated rate-limit dimension: at most 5 submissions per 15 minutes per IP address and, separately, per authenticated user (both dimensions apply; an authenticated caller is bound by both). (ratified 2026-08-18) | Confirmed |
| D-214 | Feedback rows live in a `feedback_items` table, admin-only to read (the D-180 admin-surface posture). An admin can toggle a row's **Handled** state, which stamps `handled_at`; the admin dashboard surfaces a live unhandled count. There is no submitter-facing read or edit path — a submission is fire-and-forget from the caller's side. (ratified 2026-08-18) | Confirmed |
| D-215 | Every feedback submission sends one email to the configured admin seed addresses over the existing email transport, dispatched **post-commit** (A-016 posture): the `feedback_items` row is the crash-safe record of the submission, the email is idempotent, best-effort transport layered on top, never a precondition for the row committing. | Confirmed |
| D-216 | Account deletion execution (§11) nulls a deleted account's feedback rows' `submitter_user_id` and retains the row and its message otherwise unchanged — the same anonymize-don't-erase posture D-092 applies to other retained history, run in the same executor pass. | Confirmed |
| D-217 | The feedback dialog is reachable from the AccountMenu for signed-in users and from a link on guest RSVP/vote pages for anonymous visitors — the two populations D-212 admits. It is localized in en, pt-BR, and es from the first commit (the standing i18n rule) and is fully keyboard- and screen-reader-operable per the existing WCAG 2.2 AA scope. (ratified 2026-08-18) | Confirmed |
| D-218 | The app is an installable PWA: a same-origin web manifest names it PartyUp, sets standalone display and theme/background colors, and supplies 192/512/maskable/apple-touch icons. The service worker is registered for **every** visitor at page load in production builds — amending the Phase 7 posture (PHASE7_DESIGN §5/§6) where `/sw.js` was registered only on push opt-in. Push permission and push subscription remain strictly opt-in (D-147/D-159/D-175 unchanged); only service-worker registration becomes unconditional. (ratified 2026-08-18) | Confirmed |
| D-219 | Minimal-shell offline: build-hashed app shell assets are precached, and navigations fall back to the cached shell while offline. API, guest-link, and health routes are never intercepted or cached by the service worker. A global banner announces offline state and clears on reconnect. Service worker updates activate automatically (`skipWaiting` + `clients.claim()`), with no user-facing update prompt. (ratified 2026-08-18) | Confirmed |
| D-220 | Install promotion: a dismissible first-visit banner (dismissal persisted per browser) plus an "Install app" row in the AccountMenu. Browsers that fire `beforeinstallprompt` get the native install prompt; iOS gets share-sheet instructions instead. Both surfaces are hidden once the app is already running standalone. (ratified 2026-08-18) | Confirmed |
| D-221 | Session history (Sessions > History) is the viewer's own record: sessions where the viewer was organizer or had a Going RSVP, status Completed or Canceled. Canceled entries render struck-through with a Cancelled tag and no duration emphasis; Completed entries show game, start time, duration, a masked player roster of Going participants incl. organizer (D-076 masking), and a "You organized" badge when applicable. Grouped by month, newest first, cursor-paged. Amends D-056: Canceled sessions the viewer organized or was Going in now also appear in history, marked; merely-visible sessions the viewer never joined no longer appear. Cancellation reasons are not stored or shown — no such domain field, out of scope. (ratified 2026-08-19) | Confirmed |
| D-222 | The history page shows a recap over the viewer's entire history (not just loaded pages): completed-session count, total hours together (sum of completed durations, rounded to hours), most-played game (name + times), and up to 3 most-frequent teammates (masked identities, D-076). Served by a dedicated endpoint, computed only from sessions satisfying D-221's completed arm. Hidden when count is 0. (ratified 2026-08-19) | Confirmed |
| D-223 | The history page offers text search (matches game/title and player names among loaded entries) and single-select filter chips by game and by played-with friend, derived from the loaded entries. Filtering is a client-side narrowing affordance over the fetched history, never a security boundary — D-076/visibility are already applied server-side. (ratified 2026-08-19) | Confirmed |

## 15. Open questions

No unresolved MVP product-behavior questions block technical design. Future and post-MVP choices are identified in their relevant sections.

## 16. Assumptions requiring validation

No unresolved behavioral assumptions block technical design. External validation is still required for platform game-catalog access, regional legal and privacy compliance, browser push behavior, and mobile-store policies before the relevant releases.
