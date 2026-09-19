# Apple UI implementation — September 17, 2026

This implements the main usability and visual changes from the [iOS/macOS review](ui-review-2026-09-17.md). The result keeps native platform navigation, makes conversation state more predictable, and gives both apps a quieter, consistent visual treatment. Changes are confined to the Apple client, its tests, and documentation; existing server changes in the working tree are separate.

## What changed

**Conversation experience.** Draft text, attachments, and edit state belong to a server/agent/conversation rather than a transient view. Canceling or submitting an edit restores the previous draft. Keyboard and button submission share the same availability checks; a response in progress leaves drafting available without accepting another send. Failed sends remain recoverable while offline. History loading and failures are explicit, with Retry. Message-count changes only follow the transcript when the reader is already near the bottom; otherwise a new-message affordance appears.

**Navigation and appearance.** The directory is called Agents, the transcript header shows the conversation title and agent, and agent editing is available from the header. Conversation search explicitly says that it filters loaded conversations. Find searches the open transcript; Rename is available in conversation actions and the Mac File menu. The transcript and composer share a centered 740-point content column. Agent and conversation lists use consistent plain styling, with stacked metadata at accessibility text sizes. iPhone keeps a stack; regular-width iPad uses the three-column layout. Mac columns have explicit size ranges.

**Visual consistency and accessibility.** Shared native semantic surfaces replace copied UIKit gray values on Mac. Outgoing messages use a subtle accent tint with primary text, avoiding white text on arbitrary system accent colors. Initials avatars use the same tinted treatment in lists, editors, and chat. The composer has quieter attachment controls, smaller Mac controls, and 44-point custom touch targets on iOS. Markdown headings/code treatment is shared with inter-agent messages. Dates appear at the beginning of a transcript and at day boundaries. Typing animation honors Reduce Motion. Expanded tool results can be shown in full and copied; failed tools remain visible when routine activity is hidden.

**Editing and management.** Agent, memory, default-model, and avatar editors protect changed work with a discard decision; saving disables changes. Memory and default models open transactional sheets, including from the Mac Manage window. Memory reload confirms before replacing local edits. Agent numeric inputs preserve intermediate text and validate before saving; description wraps, reasoning spells out Medium, and custom voice syntax is disclosed on demand. Agent Communication now has an explicit “Allow all agents” choice; deselecting the last individual agent cannot silently broaden access. Mac gets focused Save/Find/Rename commands and retains its Settings scene.

**Setup, media, costs, and voice.** First-run setup remains until connection succeeds; normal Settings has one relevant connection action per state. Address validation is shared. Connection notices are placed at the active compact screen or split-view root. Attachment/import/dictation failures have visible feedback, and photo/dictation preparation shows progress. Sent images and documents use native Quick Look; remote-image errors are recoverable. Costs has prominent total spend, a daily chart, localized amounts/dates, explicit USD currency, consistent completion labels, and rolling 7/30-day labels. Job statuses and agent names are friendlier; 90-minute schedules retain their minutes. Voice mode offers microphone pause/resume/retry and checks for a configured voice before starting. Mac avatar editing offers Choose File and all avatar editing offers Revert.

## Coverage of the review

“Implemented” describes the source changes in this pass; it does not imply that every device or accessibility scenario has been manually tested.

| Review item | Status | Remaining scope or qualification |
|---|---|---|
| 1. Conversation drafts | Implemented | In memory only; persistence across app restarts is not included. |
| 2. Editor saving/dismissal | Implemented | Native window-close and keyboard focus sequences still need interactive device checks. |
| 3. Loading/empty/error states | Implemented | Live-server latency and reconnect scenarios remain to validate. |
| 4. Attachment/dictation feedback | Implemented | Permission and recognition-model download paths need physical-device testing. |
| 5. Agent Communication | Implemented | “No agents” still requires disabling the Chat tool; empty arrays retain the server's “all” meaning. |
| 6. Keyboard submission | Implemented | Uses the same send guard as buttons/voice; no queueing is introduced. |
| 7. Accessibility | Partial | Touch targets, labels, large-text rows, and Reduce Motion improved. Full VoiceOver/Full Keyboard Access/contrast audit remains. |
| 8. Reading position | Implemented | Long streaming histories, keyboard resizing, and content reflow need interactive verification. |
| 9. Vocabulary/title hierarchy | Implemented | Agents, conversations, and audio-action labels are aligned. |
| 10. Search/action discovery | Partial | Header editing, Rename, list search, and transcript Find are present. Server-wide history search and message hover actions are not included. |
| 11. Mac commands | Implemented | Save/Find/Rename and existing New/Refresh/Settings are available; full focus traversal remains to check. |
| 12. Transcript/column sizing | Implemented | Minimum-width windows and collapsed columns still need interactive review. |
| 13. Lists/iPad adaptation | Implemented | iPad rotation and compact/regular transitions need device validation. |
| 14. Composer hierarchy | Implemented | Shared metrics, quieter controls, explicit edit action, and inline feedback. |
| 15. Semantic colors | Implemented | Broad accent/Increase Contrast/Reduce Transparency matrix remains. |
| 16. Agent identity | Implemented | Shared avatar fallback and consistent edit affordance. |
| 17. Transcript typography/time | Implemented | Long tables, URLs, and code need further visual stress testing. |
| 18. Activity/retry | Partial | Clearer retry/regenerate/resubmit wording and visible failures; a consolidated current-turn activity component is deferred. |
| 19. Agent editor organization | Partial | Better fields, validation, and disclosure; dedicated Mac section navigation/editor window is deferred. |
| 20. First-run setup | Implemented | Focused first-run screen and consistent connection controls. |
| 21. Connection status | Implemented | Contextual shared notices and reconnect actions. |
| 22. Management | Implemented | Manage naming, friendly metadata, accurate schedules, and retry states. Cron expressions remain available for exact schedules. |
| 23. Costs | Implemented | Chart plus accessible detail rows, clear totals/periods/currency. |
| 24. Media viewing | Implemented | Quick Look capabilities depend on file type and available attachment bytes. |
| 25. Voice | Implemented for iOS | Physical audio testing remains; Mac hands-free mode is a separate feature decision. |
| 26. Avatar/smaller polish | Partial | Choose File, Revert, labels, and date buckets done; crop/reposition and variant comparison are deferred. |

## Validation

- Both platform test builds succeeded using Xcode 27.0: **36 tests passed on iOS, 35 on macOS, zero failures**. The iOS destination was an iPhone 17 Pro simulator running iOS 26.5. `git diff --check` passed.
- State regression tests cover draft ownership/restoration, offline and streaming submission, offline retry preservation, distinct failed loads, server address validation, manual voice pause/retry, and exact schedule intervals.
- Appearance smoke tests render isolated SwiftUI fixtures and retain screenshots in the Xcode test results. iOS coverage includes light/dark conversations, an accessibility-size agent list, and first-run setup. Mac coverage includes light/dark hosted conversation content and Settings.
- Screenshots were visually inspected. They are fixture renders, not end-to-end UI automation or reference-image assertions. Mac captures omit native window/toolbar chrome; no claims about full window interactions follow from those images.
- No live server conversations, actual microphone sessions, VoiceOver sessions, or comprehensive iPad/window-size matrix were exercised. These remain the final acceptance checks before calling the complete UI polished.

## Useful follow-up work

1. Exercise live sessions, offline/reconnect behavior, editor closing, and voice on real devices; run the review's accessibility/window-size matrix.
2. Add message hover/focus actions and refine the agent editor's section navigation if those workflows are used frequently.
3. Add avatar crop/reposition, server-wide conversation search, and optional durable drafts as distinct features with their own behavior and storage decisions.

## Mac navigation follow-up

Interactive testing found two issues that the original hosted-view screenshots did not cover:

- Selecting an agent created two `.searchable` controls in one native toolbar. AppKit raised `NSInternalInconsistencyException` because the toolbar already contained `com.apple.SwiftUI.search`. Agent search stays in the toolbar; conversation search now uses a native `NSSearchField` inside its column.
- An iOS-only status inset was still installed on Mac with empty content. In the real split window it reduced the conversation list to zero height. The complete inset modifier is now conditional on iOS, rather than only its contents. Conversation search sits in an explicit vertical layout above the list and reports its native control height.

Added a native layout regression at two window heights and an end-to-end Mac UI test that selects agents, filters conversations independently, and checks that rows remain clickable. The UI test uses a DEBUG-only fixture in the real app scene, rather than a hosted content snapshot. `ACHATES_APP_BUNDLE_IDENTIFIER` can isolate test launches from a running development app; normal app identity is unchanged.

Follow-up verification: **36 Mac unit/layout tests passed**, the **full-window agent-selection/search UI test passed**, and the **iOS simulator build passed**. The UI regression asserts visible/clickable rows before and after switching agents, independent search filtering, and exactly one search field in the Mac toolbar.

## Activity-label alignment follow-up

Tool activity and thinking labels now use the same shared 12-point horizontal content inset as message text on both platforms. Progress indicators follow the label instead of moving its leading edge while work is running. The existing Mac/iOS conversation appearance fixtures include completed tools, collapsed thinking, and running tools for visual review.

## iPad Settings dismissal follow-up — September 18

Settings opened through a sidebar navigation link in the iPad split view, which could leave it without a back action. Regular-width iPad now presents Settings in its own navigation sheet with an explicit Done button. Compact iOS retains its normal push and back button; first-run setup and Mac Settings keep their existing presentation.

The network-free app-scene fixture now runs on iOS as well. The Settings UI regression passed on an iPad Pro 13-inch and an iPhone 17 Pro simulator running iOS 26.5, covering dismissal in portrait and landscape; the iPad check also closes and reopens the sheet in each orientation.

The iPad test suite reported zero failures, but Xcode stalled during result collection afterward and was stopped. The iPhone run completed normally.
