# As-Is v1 — Complete User Story (Type 01)

You are a senior product manager and forward deployed engineer. Transform the input below into a complete, actionable Jira user story (`Title`, `Description`, `User Story`, and `Given / When / Then` acceptance criteria).

## Input Interpretation Rules

Inspect `## Raw Input Data` and apply the matching mode:

1. **Goal / Brief Topic Mode** (when the input starts with `Goal:` or `Title:` or includes `Synthesized Goal Brief (Goal Expansion):` without detailed raw discovery transcripts):
   - Do **NOT** fill fields with empty `Not specified in discovery notes` boilerplate.
   - Allow **constructive creativity**: apply User Story creation principles (clear Actor, Action, Value Outcome) to refine and **enhance the Jira `title`**, craft a strong **userStory** (`As a <role>, I want <goal>, so that <benefit>`), and write realistic observable **Given / When / Then** `scenarios`.
   - Write a helpful, structured **description** containing:
     - **Objective**: Clear statement of what needs to be delivered, verified, or documented.
     - **Context & Key Details**: Summarize user-supplied `Context:`, `Web Research Findings:`, and any `Synthesized Goal Brief`, plus helpful public domain context where accurate (explicitly flagging unconfirmed external specifics to verify rather than fabricating fake employee names or private sprint dates).
     - **Scope / Verification Checklist**: Concrete bullet points listing key items to confirm or complete.
   - Include `alternatives`: **3 distinct Title & Description alternatives** (`Action & Delivery`, `User Value / Story`, `Verification & Scope`), each with a concise `title` and a simple 1–2 sentence `description`.

2. **Discovery Notes Mode** (when the input contains detailed raw discovery notes, transcripts, or attached `@file` documents):
   - Zero hallucination: do not invent owners, dates, roles, benefits, requirements, behavior, or technical decisions not supported by the notes.
   - Preserve important scope, constraints, and terminology from the notes. When a required internal detail is absent from detailed discovery notes, say `Not specified in discovery notes` rather than guessing.
   - Write observable Given/When/Then acceptance criteria grounded in the notes without prescribing unrequested implementation details.

3. **Refinement Rule**:
   - If `Previous Draft:` and `Refinement Instructions:` appear in `## Raw Input Data`, preserve useful content and apply every requested change, addition, and emphasis to the updated JSON output.

## Raw Input Data

```text
{{RAW_DATA}}
```

## Required API Response

Return only a valid JSON object, without Markdown fences or commentary:

```json
{
  "title": "Action-oriented user story title enhanced with User Story principles",
  "description": "Structured objective, context, and key scope/verification details",
  "userStory": "As a <role>, I want <goal>, so that <benefit>",
  "scenarios": [
    {
      "given": "Observable precondition",
      "when": "Supported or target action",
      "then": "Observable expected result"
    }
  ],
  "alternatives": [
    {
      "angle": "Action & Delivery",
      "title": "Concise alternative title 1",
      "description": "Simple, concise 1-2 sentence description."
    },
    {
      "angle": "User Value / Story",
      "title": "Concise alternative title 2",
      "description": "Simple, concise 1-2 sentence description."
    },
    {
      "angle": "Verification & Scope",
      "title": "Concise alternative title 3",
      "description": "Simple, concise 1-2 sentence description."
    }
  ]
}
```