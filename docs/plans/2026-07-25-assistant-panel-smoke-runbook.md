# Phase 2 Assistant Panel Smoke Runbook

## Context

Phase 2 of the Assistant threading engine restructures the chat UI by relocating the session summary and chat interface from the Session Details Assistant tab into a collapsible AssistantSidePanel in the read view. Named-thread management is now inline, and the Session Details window no longer exposes an Assistant tab. This runbook covers all user-facing interactions with the new panel, thread management, citation behavior, and evidentiary safeguards.

## Smoke Test Steps

### P2-1: Ask toggle opens/closes the panel; transcript text reflows live

**Steps:**
1. Open a session in the read view.
2. Locate the "Ask" button or toggle in the interface.
3. Click it to open the AssistantSidePanel.
4. Observe the panel slides in from the right side.
5. Verify the transcript text reflows to accommodate the panel width (no horizontal scroll appears).
6. Click the "Ask" toggle again to close the panel.
7. Observe the panel slides out; transcript expands back to full width.

**Expected result:** Panel opens and closes smoothly on toggle. Transcript reflows dynamically without horizontal scrollbars appearing in either state.

---

### P2-2: Splitter drag resizes; width respects min 280 / max 60%; state persists

**Steps:**
1. Open the AssistantSidePanel in the read view.
2. Locate the resize splitter (vertical line) between the transcript and panel.
3. Drag the splitter left to shrink the panel.
4. Verify the panel does not go below 280 pixels wide.
5. Drag the splitter right to expand the panel.
6. Verify the panel does not exceed 60% of the window width.
7. Close and reopen the application.
8. Verify the panel reopens at the same width you set (check window-state.json contains assistantPanel width).
9. Close the panel explicitly with the toggle.
10. Reopen the application.
11. Verify the panel opens closed and does not restore until you toggle it again.

**Expected result:** Splitter respects min/max constraints. Width persists across app restarts. Explicit toggle state is remembered separately from width.

---

### P2-3: Heuristic: empty session closes panel; session with history opens it

**Steps:**
1. Create a new session (or use one with no summary/chat history).
2. Open it in the read view.
3. Verify the AssistantSidePanel is closed by default (no panel visible).
4. Open a session that has existing summary and chat messages.
5. Verify the panel opens automatically before any explicit toggle.
6. Close the application.
7. Delete assistantPanel key from window-state.json.
8. Reopen the application and reload that session.
9. Verify the heuristic applies again (panel closed for empty, open for history).

**Expected result:** Sessions without chat history keep the panel closed. Sessions with history open it automatically. Deleting window-state.json key resets the heuristic.

---

### P2-4: Summary expander: versions switch, Regenerate streams, stale badge shown

**Steps:**
1. Open a session with an existing summary in the AssistantSidePanel.
2. Locate the Summary section with version buttons or a version selector.
3. Click on a different summary version (if multiple exist).
4. Verify the summary text changes instantly to that version.
5. Locate the "Regenerate" button.
6. Click Regenerate.
7. Verify a "draft" label appears on the summary header.
8. Watch the summary section stream new text in real-time.
9. Once generation completes, verify a "stale" badge or indicator appears on both the summary header and in the summary body (or marker text shows).
10. Switch to another summary version to clear the stale indicator.
11. Switch back to the generated version to confirm the stale badge remains until explicitly refreshed or regenerated again.

**Expected result:** Version switching is instant. Regenerate shows a draft label during streaming. Stale badge appears and persists on both header and body after generation completes.

---

### P2-5: Threads: New, Rename, Archive, Show archived, Unarchive

**Steps:**
1. Open the Threads section in the AssistantSidePanel.
2. Click "New" or a new-thread button.
3. Verify a thread is created with the name "Chat 1" (or "Chat N" for the Nth thread).
4. Right-click or locate an inline Rename option on that thread.
5. Double-click or activate rename mode.
6. Type a new name and press Enter.
7. Verify the name changes immediately.
8. Press Escape during a rename; verify the name reverts to the original.
9. Right-click on a thread and select "Archive" or locate an archive button.
10. Verify the thread disappears from the active thread list.
11. Locate and click "Show archived" or an equivalent option.
12. Verify archived threads appear with "(archived)" text in read-only mode.
13. Try to ask a question in an archived thread.
14. Verify the "Ask" input is disabled.
15. Right-click an archived thread and select "Unarchive".
16. Verify the thread reappears in the active thread list with full functionality restored.

**Expected result:** New threads auto-name as "Chat N". Rename is inline with Enter-to-commit and Esc-to-cancel. Archive hides threads and disables Ask. Show archived displays them as read-only. Unarchive restores full functionality.

---

### P2-6: Thread switch is instant; no transcript reload, no model re-prime

**Steps:**
1. Open the AssistantSidePanel with multiple threads in the session.
2. Switch between threads by clicking on them.
3. Verify the summary and chat history change instantly without the transcript reloading.
4. Ask a question in one thread.
5. While the model is priming or generating, switch to another thread.
6. Ask a question in the second thread.
7. Observe that the model does not re-prime or restart; generation proceeds smoothly in the new thread context.
8. Verify no transcript flicker or audio re-load happens.

**Expected result:** Thread switching is instant. Transcript does not reload. Model stays primed across thread switches within the same session scope.

---

### P2-7: Citation chip click scrolls THIS window's transcript; find bar opens

**Steps:**
1. Ask a question in the AssistantSidePanel that generates a response with citations (if available).
2. Locate a citation chip or linked term in the chat response.
3. Click on the citation chip.
4. Verify the transcript in the read view scrolls to highlight or reveal the referenced segment.
5. Verify the find bar (Ctrl+F) opens in the read view window (not a second window).
6. Verify the search term from the citation is populated in the find bar.
7. Try clicking a different citation.
8. Verify only the one read view updates; no second read-view window appears.

**Expected result:** Citations click to scroll and highlight in the current read view. Find bar opens with the citation term. No duplicate read-view windows are created.

---

### P2-8: Recording start cancels in-flight answer; question kept, nothing persisted

**Steps:**
1. Ask a question in the AssistantSidePanel.
2. While the model is generating a response, click the Record button (or start a new recording).
3. Verify the in-flight response stops generating and is discarded (does not appear in the thread).
4. Verify the question you asked remains in the chat history (not deleted).
5. Verify no partial response is saved to chats.json.
6. Try asking again after recording stops.
7. Verify the thread is clean and ready for new interactions.

**Expected result:** Recording start cancels generation in-flight. The question persists, but the partial response is not saved. Thread is clean for resumption.

---

### P2-9: Session Details no longer has Assistant tab; Details/Speakers/Matters work

**Steps:**
1. Open a session and access Session Details (via a menu or button).
2. Look at the tab bar or sections in the Session Details window.
3. Verify there is no "Assistant" tab.
4. Verify the "Details" tab is present and shows session metadata.
5. Verify the "Speakers" tab is present and allows speaker management.
6. Verify the "Matters" tab is present and shows matter associations.
7. Test adding/editing speakers and matters.
8. Verify all three tabs function as before.
9. Close Session Details.
10. Return to the read view and verify the AssistantSidePanel is the only place for chat/summary.

**Expected result:** Session Details no longer has an Assistant tab. Details, Speakers, and Matters tabs all work normally. Chat and summary are only accessible from the read view AssistantSidePanel.

---

### P2-10: Long thread triggers condense; "Earlier turns were condensed" indicator and Recap appear

**Steps:**
1. Open a session and create a new thread in the AssistantSidePanel.
2. Ask multiple questions (at least 6-10 turns) to build up conversation history.
3. After enough turns, watch the chats.json or monitor the chat interface.
4. Observe that a "Earlier turns were condensed" message or indicator appears in the thread.
5. Verify this indicator is inserted into the chat history without removing the original turns.
6. Examine chats.json for the thread (use a text editor or jq).
7. Verify the thread now contains a "Recap" or similar field with condensed content.
8. Verify no earlier turns are deleted; the Recap is an additional entry or metadata field.
9. Ask a new question and verify the model uses the Recap for context (no repeated turns).
10. Verify the Recap label or indicator is only shown once at the condense boundary.

**Expected result:** Long threads automatically condense. An indicator appears in the chat UI. chats.json gains a Recap field. No turns are deleted; condensation is purely metadata-driven. New questions use the condensed context.

---

## Phase 3: Matter-Scoped Assistant Panel

### P3-1: Matter Ask toggle opens the chat-only panel; no Summary expander; state persists per "matters" key

**Steps:**
1. Open the Matters page (main window, Matters tab).
2. Select a matter.
3. Locate the "Ask" button or toggle in the matter view.
4. Click it to open the AssistantSidePanel.
5. Verify the panel opens on the right side of the matter view.
6. Verify the panel shows only chat history and message input (no Summary expander).
7. Close the panel by clicking the toggle again.
8. Verify the toggle state is persisted in window-state.json under the "matters" key.
9. Close and reopen the application.
10. Navigate back to the same matter.
11. Verify the panel reopens in the state you left it (open or closed).
12. WATCH: Close the main window entirely and reopen it with the Matters panel open; verify the panel renders at full width (no narrow-slit regression).

**Expected result:** Matter Ask toggle opens/closes the panel showing chat only (no Summary). State persists per-matter via the "matters" key in window-state.json. Panel renders at full width after window reopen.

---

### P3-2: Matter switch swaps panel threads/history; warm helper torn down

**Steps:**
1. Open the Matters page with the AssistantSidePanel open.
2. Select Matter A and ask a question.
3. Verify the response appears in the panel.
4. Switch to Matter B (click another matter in the list).
5. Verify the panel instantly shows Matter B's thread history (or empty if none exists).
6. Verify the transcript in the main window does NOT reload.
7. Verify no transcript flicker occurs.
8. Ask a question in Matter B.
9. Verify the model primes (warm helper for Matter A is torn down).
10. Switch back to Matter A.
11. Verify Matter A's previous response is still there, thread intact.

**Expected result:** Matter switch is instant. Panel history swaps. Transcript does not reload. Warm helper is torn down on matter switch; next ask re-primes.

---

### P3-3: Coverage disclosure renders inside the panel after an answer

**Steps:**
1. Open the Matters page with the AssistantSidePanel open.
2. Ask a question in the matter's thread.
3. Wait for the response to complete.
4. Verify a coverage disclosure badge or indicator appears inside the panel (below or alongside the response).
5. Verify the disclosure shows the session(s) or content range used to generate the answer.
6. Close the panel and reopen it.
7. Verify the coverage disclosure persists with the saved response.

**Expected result:** Coverage disclosure renders inside the panel after generation completes. Disclosure persists across panel close/reopen.

---

### P3-4: Sessions-tab Summary column shows chips; click opens read view; Generate starts generation

**Steps:**
1. Open the Matters page and select a matter.
2. Navigate to the Sessions tab for that matter.
3. Locate the Summary column in the sessions grid.
4. Verify sessions with a generated summary show a "Done" chip.
5. Verify sessions with a stale summary show a "Caution" chip (yellow/warning color).
6. Verify sessions with no summary show a "Generate" link.
7. Click a "Done" or "Caution" chip.
8. Verify the session opens in the read view.
9. Verify the AssistantSidePanel opens alongside the transcript.
10. Close the read view.
11. Return to the Matters Sessions tab.
12. Click a "Generate" link.
13. Verify the session opens in the read view.
14. Verify the panel opens AND generation starts immediately (no manual click required).
15. Wait for generation to complete.
16. Verify the summary appears in the panel and the Sessions tab chip updates to "Done".

**Expected result:** Summary column shows Done/Caution/Generate. Chip/link click opens read view with panel. Generate link also starts generation. All transitions are smooth with no duplicate windows.

---

### P3-5: Tab strip stays one row at minimum window width; scrolls, never wraps

**Steps:**
1. Open the Matters page with the AssistantSidePanel open.
2. Navigate to the Sessions tab.
3. Slowly resize the main window to its minimum width.
4. Verify the tab strip (Details/Sessions/Vocabulary/Advanced) remains on a single row.
5. Verify the tabs scroll horizontally if needed (scroll buttons or scroll area appear).
6. Verify no tab wraps to a second row.
7. Expand the window back to full width.
8. Verify tabs return to full visibility and scroll controls disappear (if not needed).

**Expected result:** Tab strip always stays one row, even at minimum window width. Horizontal scrolling is available; no wrapping occurs.

---

### P3-6: Matter Assistant tab is gone; Details/Sessions/Vocabulary/Advanced intact

**Steps:**
1. Open the Matters page.
2. Select a matter.
3. Examine the tab bar or sections (Details/Sessions/Vocabulary/Advanced).
4. Verify there is no "Assistant" tab.
5. Click the Details tab.
6. Verify session metadata and details render correctly.
7. Click the Sessions tab.
8. Verify the sessions grid with Summary column loads and functions.
9. Click the Vocabulary tab.
10. Verify vocabulary list and management work.
11. Click the Advanced tab.
12. Verify advanced settings or options are present.
13. Return to the read view and verify the AssistantSidePanel is the only place for chat.

**Expected result:** Matter view has no Assistant tab. Details, Sessions, Vocabulary, and Advanced tabs are all present and functional. Chat access is only via the panel on the read view.
