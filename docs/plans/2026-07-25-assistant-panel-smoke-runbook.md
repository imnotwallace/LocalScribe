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
