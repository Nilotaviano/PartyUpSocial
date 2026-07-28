# Game Session Scheduler — Product Specification

**Status:** Discovery in progress
**Last updated:** 2026-07-28
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

### Candidate goals — not yet confirmed

- Encourage or improve response rates without becoming annoying.

## 4. Non-goals

- Public matchmaking or player discovery with strangers.
- Replacing the existing real-world friendship network.

Possible additional boundaries to decide:

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
- Unless a session is private, the organizer, pending invitees, approved participants, Going and Maybe participants, and waitlisted registered users extend visibility to their friends. Unapproved join requesters do not. A declined invitation, rejected request, or departed participant no longer extends visibility.
- Anyone who can see a session can see its attendee list and individual responses.
- Anyone who can see a session can also see its notes before requesting to join.
- A session can instead be private. It is visible to its invitees plus any participants and outstanding requesters retained when an existing session becomes private.
- If a visible session becomes private, existing non-invited participants and outstanding requests remain, but friends who only had access through those people immediately lose access.
- A shareable link can be used to invite someone.
- A link invitee can respond as a guest without creating an account; only a display name is initially required.
- A guest can revisit the same link to revise their response.
- Every guest receives a unique invitation link. It expires after the session and cannot be renewed.
- Private sessions can use guest links. A guest link is a transferable bearer credential: forwarding it gives the recipient control of that single guest identity and response.
- An organizer can remove confirmed or pending guests; removal immediately revokes that guest's link.
- Users can find one another through profile links, QR codes, and friend codes.
- Users can import their Steam friend list. Other platform imports may be added later.
- Steam import suggests matched registered users for mutual friend approval and offers optional invitation links for unmatched friends.

The minimum registered-user profile contains:

- Display name.
- Unique username.
- Avatar.
- Linked gaming platforms and handles.
- Specified time zone.

User-uploaded avatar images are not allowed. A linked platform account, initially Steam, can supply the avatar; when several accounts are linked, the user selects the active platform avatar. An unlinked account can select one emoji as its avatar.

Blocking is required in the initial release. A blocked user cannot discover the blocker or gain access to new sessions through the blocker's participation. Existing shared sessions organized by a third person are the exception described below. Comments are outside the product. Mobile-store reporting requirements for the remaining user-authored profile and session fields are tracked in section 8.6.

Blocking is mutually invisible and removes any existing friendship. In a session organized by a third person, both users retain access but each sees **Anonymous User** in place of the other. If a participant blocks an organizer, the participant leaves that organizer's sessions. If an organizer blocks a participant, that participant is removed from the organizer's sessions.

To be defined:

- Persistent named groups are outside the initial release but may be added later.
- How duplicate guest names are handled.
- How a guest is reconciled with a later registered account.
- Friend-code lifecycle and privacy.
- Exact reciprocal visibility and session behavior when blocking occurs.
- Whether minors are an intended audience.

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
   A non-private session requires at least one directly invited registered friend when created; a guest link alone does not satisfy this requirement.
4. Registered or guest invitees respond **Going**, **Maybe**, or **Declined**.
5. Unless the session is private, eligible friends can see it and request to join.
6. Every join request requires organizer approval. A requester does not choose an attendance intent; approval marks them Going, or Going-waitlisted if capacity is full. Invitations can also be added after creation.
7. The organizer and Going guests count toward minimum attendance. Both Going and Maybe participants consume optional maximum capacity.
8. When optional maximum capacity is reached, new Going or Maybe responders enter the waitlist with their selected attendance status.
9. If capacity becomes available, the organizer chooses whom to promote. Promoting either a Going- or Maybe-waitlisted participant fills the slot immediately while preserving their attendance status.
10. When the number of Going participants reaches the configured minimum, the session becomes Confirmed automatically.

If the organizer changes the session's start time, confirmation is cleared, the waitlist is cleared, and everyone returns to Pending. Changes to other details do not reset responses.

Going and Maybe participants can leave a session. If Going attendance falls below the minimum, the session remains Confirmed while any Going- or Maybe-waitlisted candidate remains. It becomes unconfirmed only after the organizer rejects every remaining waitlisted candidate. After the start time, a Confirmed session remains Confirmed and will Complete even if attendance falls below minimum; an unconfirmed in-progress session may still Confirm if Going reaches minimum before the end. Only the organizer can cancel a session. A Confirmed session becomes Completed automatically after its end time; an unconfirmed past session becomes Expired. Only Completed sessions remain in history.

### 7.2 Share availability

1. A user publishes one-time or recurring blocks during which they are available.
2. Eligible friends can see those availability blocks.
3. The user selects friends and a date/time range.
4. The product searches the next seven days by default, calculates continuous full and partial overlap windows, and ranks them by the number of selected friends who are available, using Maybe count to break ties. All selected friends are optional; desired session duration is not an input and results are not artificially limited.
5. A recurring block follows a weekly schedule. The owner can delete one occurrence or the entire recurring series.

Availability uses specific time blocks and is visible to all friends in the initial release. Overlapping blocks are visually merged and treated as one continuous interval. A recommendation can create either a prefilled fixed-time session or a prefilled poll. The organizer may choose a duration beyond the recommended overlap and receives a warning.

### 7.3 Poll multiple candidate times

1. An organizer creates a poll containing multiple possible start times and chooses a 6-, 12-, 24-, or 48-hour voting deadline.
2. Registered or guest invitees mark each option **Available**, **Maybe**, or **Unavailable**. Voters can suggest additional times.
   Suggested times become poll options immediately without organizer approval.
3. Identical date-times are deduplicated and a poll can contain at most 12 options. The organizer can delete any option without notifying its voters.
4. Voting closes at the deadline and waits for the organizer to select an option; there is no automatic winner. If no option is selected within 48 hours after the deadline, the poll expires.
5. The selected option becomes the session's fixed start time. Available voters become Going, Maybe voters remain Maybe, and Unavailable voters become Declined.

The polling lifecycle is fully defined for the initial release.

A non-private poll follows the same inherited visibility rules as a fixed session and requires at least one directly invited registered friend. Eligible friends can request to join while it is Polling; approval makes them a voter with access to all current options. A private poll may instead be created using only guest links.

When a poll is finalized with more Available and Maybe voters than maximum capacity, allocate active capacity in this order: organizer, Available voters ordered by response time, then Maybe voters ordered by response time. Overflow enters the matching Going or Maybe waitlist state.

## 8. Functional requirements

### 8.1 Scheduling

Confirmed:

- A session may be created directly for a fixed start time.
- A session has a duration.
- Invitees can respond **Going**, **Maybe**, or **Declined**.
- Users can publish one-time or recurring availability blocks.
- Availability blocks use exact start and end times and are visible to all friends in the initial release.
- Recurring availability follows a weekly pattern and supports deleting one occurrence or the whole series.
- The product calculates availability overlaps and recommends times.
- An organizer can poll invitees about multiple candidate times.
- Poll responses are Available, Maybe, and Unavailable; guests can vote and all voters can suggest times.
- Poll deadlines are organizer-selected durations of 6, 12, 24, or 48 hours.
- Polls support at most 12 distinct date-time options. Duplicate date-times are merged, and the organizer can delete any option without notifying its voters.
- At the deadline, voting closes and the organizer selects the final option manually. The poll expires after 48 additional hours without a selection.
- Non-private polls use normal inherited visibility, accept join requests during Polling, and require one directly invited registered friend. Approved requesters become voters with all current options.
- Private polls can be created with guest links only.
- Poll finalization allocates capacity to the organizer first, then Available voters by response time, then Maybe voters by response time; overflow is waitlisted with matching attendance status.
- Voter-suggested times become poll options immediately without organizer approval.
- Choosing a poll option maps Available to Going, Maybe to Maybe, and Unavailable to Declined.
- Availability recommendations default to the next seven days, accept a custom date/time range, return all continuous full and partial overlap windows, rank first by the number Available, and use the number Maybe to break ties. Duration is not an input and every selected friend is optional.
- Overlapping availability blocks are merged visually and algorithmically into continuous intervals.
- A recommendation can prefill either a fixed-time session or a poll.
- A fixed-session duration may extend beyond its recommended overlap; the UI warns but allows it.
- The organizer has final judgment over whether a session is viable.
- Sessions can specify a minimum Going count and a maximum capacity.
- Minimum attendance is required; maximum capacity is optional and, when present, cannot be lower than minimum attendance.
- The organizer counts toward attendance thresholds only while Going; Going guests count like registered Going participants.
- The organizer starts as Going and may change to Maybe or Declined while remaining the organizer. Ownership transfer is outside the initial release.
- A session becomes Confirmed automatically when its minimum Going count is reached.
- Sessions can have a waitlist.
- Going and Maybe both consume optional maximum capacity; only Going counts toward minimum confirmation attendance.
- At maximum capacity, new Going or Maybe responses enter the waitlist with their selected status. Existing active Maybe participants remain active.
- The organizer manually chooses whom to promote when capacity becomes available.
- Promoting either a Going- or Maybe-waitlisted participant fills the available slot immediately and preserves their selected status.
- Changing a fixed session's start time clears confirmation and the waitlist and returns every participant to Pending; changing other details does not reset responses.
- Recurring sessions are outside the initial release.
- Going and Maybe participants can leave.
- After a previously Confirmed session drops below minimum, it remains Confirmed while at least one Going- or Maybe-waitlisted candidate remains. It becomes unconfirmed and notifies everyone only when the organizer rejects all remaining waitlisted candidates.
- Only the organizer can cancel a session.
- A Confirmed session becomes Completed automatically after its end time. An unconfirmed past session becomes Expired. Only Completed sessions remain in history; Canceled and Expired sessions do not.
- Completed-session history shows the final RSVP roster. Separate actual-attendance tracking is not supported.
- A session is created either directly at a fixed time or in Polling mode. Polling is optional and never required for direct scheduling.
- Availability and recommendations use 15-minute increments.
- Fixed-time invitation responses close when the session starts.
- Pending responses remain Pending at session start. Going and Maybe participants can continue changing between those attendance states while the session is in progress.
- Once a Confirmed session starts, later attendance changes cannot unconfirm it and it will become Completed. An unconfirmed session may still Confirm until its end time.
- Join requests do not include attendance intent. Approval makes the requester Going, or Going-waitlisted when maximum capacity is full.
- Join requesters can withdraw pending requests. Acceptance and rejection both notify the requester, and an accepted requester can subsequently change attendance normally.
- A non-private session must include at least one directly invited registered friend at creation.
- Reducing maximum capacity below current Going + Maybe occupancy is forbidden. Increasing maximum leaves waitlist promotion manual.
- Changing minimum attendance immediately recalculates confirmation and sends the corresponding confirmation/unconfirmation notification.
- Changing duration sends a rescheduling notification without resetting RSVPs.
- Changes to title, game, platform, notes, maximum capacity, or privacy do not generate notifications.

To be decided:

- Rescheduling and cancellation rules.
- Handling of late response changes.

### 8.2 Games and session details

Confirmed session details:

- Required title.
- Start date and time.
- Duration.
- Platform.
- Optional game.
- Optional notes.
- Required minimum attendance.
- Optional maximum player count.

The initial release supports one platform per session, selected from:

- Steam.
- PlayStation.
- Xbox.
- N/A.

For Steam, PlayStation, or Xbox, the user can choose a game from that platform's full catalog or enter a free-text value. Catalog availability and licensing must be validated for each platform before implementation; free text alone is an acceptable initial fallback wherever a complete reliable catalog is unavailable. For N/A, the game field is free text.

To be decided:

- Whether sessions require a title separate from the game.
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
- Profile links, QR codes, and friend codes initiate friendship.
- Steam friend-list import is supported initially; other platform imports may follow.
- Arbitrary avatar uploads are not supported. Avatars come from linked platform accounts; unlinked accounts use a selected emoji.
- Registered users can authenticate with email and password, passwordless email, Steam, Google, or Apple.
- Email can be used for account recovery.
- Social-only accounts are allowed and are not required to add a separate verified email.
- Users can link multiple authentication methods after registration. The UI does not warn when only one method remains connected.
- Before a friendship is accepted, only the unique username is visible.
- Friend-code rotation is outside the initial release.

To be decided:

- Future persistent-group roles and permissions.
- Blocking and removal.
- Guest-link security, guest identity reconciliation, and abuse controls.
- External platform linking, token storage, unlinking, and import refresh behavior.
- Account verification, credential linking, recovery safeguards, and handling duplicate identities across login methods.

### 8.4 Communication and notifications

The initial web product will use in-app notifications and Web Push notifications on supported desktop and mobile browsers. Native mobile push notifications are expected when the native apps are built. Email, Discord, WhatsApp, and other channels may be added later.

Push notifications are initially generated for:

- Session invitations.
- Session confirmation.
- Session becoming unconfirmed.
- Session rescheduling.
- Join request received by an organizer.
- Join request accepted or rejected.
- Poll invitation.
- Voter-suggested poll option.
- Approaching poll deadline.
- Poll closed awaiting organizer selection.
- Poll option selected.
- Added to, promoted from, or rejected from a waitlist.
- A waitlist vacancy requiring organizer action.
- A received friend request.
- A Steam-import match.
- One configurable reminder per user per session.

Friend-request acceptance, friend removal, and blocking do not generate notifications.

Session comments are not supported.

- Muting a session suppresses every notification, including cancellation, except the configured pre-session reminder when the user is Going or Maybe. Choosing reminder Off suppresses that reminder too.
- The reminder defaults to one hour before the session. Each user can choose Off, 15 minutes, 30 minutes, 1 hour, 3 hours, 12 hours, or 24 hours.

To be decided:

- Unread state, notification grouping, and user preferences.
- Notification behavior when details change.
- Fallback behavior when push permission is denied or browser push is unavailable.

### 8.5 Calendars and time zones

Each user specifies a time zone. All dates and times are displayed in the viewer's specified time zone. The product does not detect or specially handle travel.

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

- Remove the account's friendships and future-session participation immediately.
- Show the deleted user as **Anonymous User** in retained Completed-session history.
- Cancel future sessions owned by the deleted account.
- Expire outstanding polls owned by the deleted account.

Current official policy references:

- [Apple App Review Guidelines — User-Generated Content and Privacy](https://developer.apple.com/app-store/review/guidelines/)
- [Google Play — User-Generated Content](https://support.google.com/googleplay/android-developer/answer/9876937)
- [Google Play — Account Deletion Requirements](https://support.google.com/googleplay/android-developer/answer/13327111)
- [Google Play — Target Audience and Content](https://support.google.com/googleplay/android-developer/answer/9867159)
- [Apple — Declared Age Range API](https://developer.apple.com/documentation/DeclaredAgeRange)
- [Android — Play Age Signals API](https://developer.android.com/google/play/age-signals/use-age-signals-api)

The stores do not impose one universal minimum user age. The initial product is **13+ and not directed to children**. It must declare an accurate target audience and satisfy the applicable child-privacy rules in every launch region, subject to legal review before launch.

Age gating uses the operating system or store-provided age-range signal when available: Apple's Declared Age Range API on iOS and Play Age Signals on Android. When no platform signal is available, including on the web, the user self-attests with an “I am at least 13 years old” checkbox. The service stores only whether the 13+ gate passed and the source of that result; it does not store a birth date.

## 9. Platform scope

### Phase 1: Web

The initial client is a responsive website with Web Push where the browser supports it. Installable PWA behavior is not required. Exact browser support and accessibility targets remain to be defined.

The signed-in home screen prioritizes:

1. Pending invitations and join requests.
2. Polls requiring the user's vote.
3. Upcoming sessions.
4. A **Find a time** action and automatic all-friends availability-overlap suggestions.

Scheduling is available through both a week-calendar view and a chronological agenda view. The calendar can combine personal availability, friends' availability, fixed sessions, and poll options, with toggles for each layer.

### Future: Android and iOS

The product and API should not unnecessarily prevent native mobile clients. Whether the mobile clients will be native, cross-platform, or packaged web applications is intentionally undecided.

## 10. Business model and administration

The initial product will be publicly available, open for direct registration, and free to use. Invitations are also supported.

To be decided:

- Whether monetization may be introduced later.
- Staffing and service-level expectations for required content/user reports.
- Detailed moderation administration tools.
- Data-retention periods and deletion/anonymization behavior for shared session content.

## 11. Quality attributes

Quality targets and further details remain to be defined for:

- Accessibility.
- Privacy and security.
- Performance.
- Availability and reliability.
- Localization. The initial UI supports English, Portuguese (Brazil), and Spanish, including locale-appropriate dates, times, and week formatting.
- Supported browsers and devices.
- Expected scale.

## 12. Success criteria

No measurable success criteria have been confirmed.

Candidate measures include:

- Percentage of proposed sessions that become confirmed.
- Time from session proposal to confirmation.
- Participant response rate.
- Confirmed-session attendance rate.
- Repeat scheduling by the same group.

## 13. Delivery boundaries

The minimum viable product and the order of later releases are not yet defined.

## 14. Decisions

| ID | Decision | Status |
|---|---|---|
| D-001 | The product coordinates multiplayer game sessions among friends. | Confirmed |
| D-002 | The web interface is the first delivery target. | Confirmed |
| D-003 | Android and iOS support is planned for later. | Confirmed |
| D-004 | The product serves existing friends; stranger discovery and public matchmaking are out of scope. | Confirmed |
| D-005 | An organizer can create a session for a fixed time and invite friends. | Confirmed |
| D-006 | Invitees can respond Going, Maybe, or Declined. | Confirmed |
| D-007 | Unless a session is private, eligible non-invited friends can see it and request to join. | Confirmed |
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
| D-018 | The organizer, pending invitees, approved participants, and waitlisted users extend a non-private session's visibility; unapproved requesters, Declined users, and Rejected users do not. | Confirmed |
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
| D-042 | Initial pushes cover invitations, confirmation, unconfirmation, rescheduling, and a configurable reminder. | Confirmed |
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
| D-056 | Only organizers can cancel; Confirmed sessions complete after their end time, unconfirmed past sessions expire, and only Completed sessions remain in history. | Confirmed |
| D-057 | Every session requires a title. | Confirmed |
| D-058 | N/A uses a free-text game; free text is an acceptable fallback when a reliable platform catalog is unavailable. | Confirmed |
| D-059 | Blocking is required; mobile UGC controls remain a release gate for the remaining user-authored fields. | Policy-required |
| D-060 | Initial localization supports English, Portuguese (Brazil), and Spanish with locale-aware date and time formatting. | Confirmed |
| D-061 | Changing the user's specified time zone moves weekly availability to the same local clock time in the new zone. | Confirmed |
| D-062 | A Going organizer and Going guests count toward minimum; Going and Maybe count toward optional maximum, which cannot be below minimum. | Confirmed |
| D-063 | A start-time change clears confirmation and the waitlist and returns everyone to Pending. | Confirmed |
| D-064 | Polling is an optional creation mode; organizers can instead schedule a fixed-time session directly. | Confirmed |
| D-065 | Blocking is mutually invisible and removes friendship; it does not generally remove users from shared sessions. | Confirmed |
| D-066 | Authors can edit/delete their comments and session organizers can delete any session comment. | Superseded by D-073 |
| D-067 | Initial authentication supports password, passwordless email, Steam, Google, and Apple; email supports recovery. | Confirmed |
| D-068 | Before friendship approval, a profile exposes only its unique username; friend-code rotation is outside MVP. | Confirmed |
| D-069 | Users can mute notifications per session and customize the default one-hour reminder. | Confirmed |
| D-070 | Poll organizers choose a 6-, 12-, 24-, or 48-hour voting deadline. | Confirmed |
| D-071 | Availability entry and recommendations use 15-minute increments. | Confirmed |
| D-072 | Account deletion is available in-app and through a public web request route. | Policy-required |
| D-073 | Session comments are not supported. | Confirmed |
| D-074 | A previously Confirmed session remains Confirmed while any Going- or Maybe-waitlisted candidate remains and becomes unconfirmed only when all are rejected. | Confirmed |
| D-075 | The organizer begins Going, may change attendance, and cannot transfer ownership. | Confirmed |
| D-076 | Blocked users retain third-party shared-session access but see one another as Anonymous User. | Confirmed |
| D-077 | Blocking between an organizer and participant removes the participant from that organizer's sessions in either direction. | Confirmed |
| D-078 | Social-only accounts do not require a separate verified recovery email. | Confirmed |
| D-079 | Poll voting closes at its deadline and waits for organizer selection. | Confirmed |
| D-080 | Polls allow 12 distinct options, deduplicate identical times, and let organizers delete any option. | Confirmed |
| D-081 | Fixed-session responses close at the session start. | Confirmed |
| D-082 | Each user gets one reminder chosen from Off, 15m, 30m, 1h, 3h, 12h, or 24h; default is 1h. | Confirmed |
| D-083 | The initial audience is 13+ and not directed to children. | Confirmed |
| D-084 | Going and Maybe both consume maximum capacity; at capacity, new Going or Maybe responses are waitlisted without displacing active Maybes. | Confirmed |
| D-085 | The organizer continues organizing and extending visibility while Maybe or Declined. | Confirmed |
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
| D-099 | A non-private session requires at least one directly invited registered friend when created. | Confirmed |
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

## 15. Open questions

### Round 10 — poll visibility, capacity mutations, and notification recipients

1. Automatic all-friends suggestions could be large. Show the top 5 windows on Home with **See all** opening the complete result list?
2. For the friends' availability calendar layer, should users select which friends to display, with none selected by default to avoid clutter?
3. Does a non-private active poll follow normal session visibility, allowing friends of invitees/voters to discover it before a time is selected?
4. Can someone request to join during Polling? If approved, do they become a voter and receive every current option?
5. When a poll finalizes with more Available/Maybe voters than maximum capacity, proposed allocation: organizer first, then Going/Available voters, then Maybe voters; overflow enters the matching waitlist. Within each group, use response time. Accept?
6. Who receives a voter-suggested-option push: all poll invitees or only the organizer? Who receives “poll closed”: organizer only or everyone?
7. When is “deadline approaching”: one hour before every poll deadline, or a duration-sensitive rule?
8. Should reducing maximum capacity below current Going+Maybe occupancy be forbidden? Increasing it would leave promotion manual.
9. Does changing minimum attendance immediately recalculate Confirmed/unconfirmed state and send the corresponding notification?
10. A duration change does not reset RSVPs. Should it send a rescheduling notification?
11. Which other edits notify participants: title, game, platform, notes, minimum, maximum, and privacy?
12. Must guests pass the 13+ age gate before viewing or responding through a link?
13. Can a private session be created with only guest links and no registered friend invitee?
14. Does a non-private poll also require at least one directly invited registered friend at creation?
15. When a transferable guest link is used on multiple devices, should last write win, with every device continuing to control that guest until revocation/expiry?

## 16. Assumptions requiring validation

- Guests will not extend visibility because they have no registered friendship graph.
- Poll selection will produce a fixed session rather than being a separate permanent event type.
- Home shows five automatic all-friends suggestions and exposes the complete list through See all.
- The friend-availability calendar layer requires explicit friend selection.
- Non-private polls use the same inherited visibility and join-request model as fixed sessions.
- Poll finalization allocates capacity to Going before Maybe and waitlists overflow deterministically.
- Reducing maximum below current occupancy is forbidden.
- Guests must pass the same 13+ gate before viewing session details.
- Multi-device guest-link edits use last-write-wins behavior.
- The first web interface should work on both desktop and mobile browsers.
