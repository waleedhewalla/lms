# Internal correspondence: types and workflows

Every internal letter has a **type**. The type says who may start it, what it must contain, and which workflow it follows from creation to closure. The catalogue and the workflows are data: the university edits them (`PATCH /api/correspondence/types/{id}`, `POST /api/workflows/definitions/{id}/new-version`) without a release. `POST /api/correspondence/types/seed-defaults` creates the standard twelve and their workflows; anything the university already has is left alone.

## Life of a letter

```
Draft ──submit──▶ Submitted ──(each step approves)──▶ Closed
  ▲                  │  │
  │                  │  └─ reject (reason required) ─▶ Rejected   (final)
  └── resubmit ◀── Returned ◀─ return for changes (reason required)
Draft / Submitted / Returned ── withdraw (author, reason) ─▶ Closed
any unfinished letter ── close (records:manage, reason) ─▶ Closed
```

- **Approved** appears only for letters without a type (the original one-reviewer path) or a workflow with no closing step. A typed letter ends at **Closed**.
- **Return** stops the run and sends the letter back to its author with the reviewer's words. The author edits it (`PATCH`) and resubmits; a resubmission is a new round that starts from the first step. *(Assumption, see "Decisions to confirm".)*
- **Reject** is final. The author is told why.
- Every step, decision, comment and notification is kept: `GET /api/correspondence/{id}/history`.

## Rules every workflow follows
- **Deadlines** are in **working days**. The weekend is Friday and Saturday (`Workweek__WeekendDays`), and any calendar event of kind *Holiday* is skipped. A step that passes its deadline is marked breached, reminded after 48 hours and escalated after 72 hours (existing SLA monitor).
- **Notifications:** the approver is notified in the app when a step reaches them; the author is notified when the letter is returned, rejected, completed or closed by someone else.
- **No self-approval:** a step is skipped when its approver is the sender or already approved the same letter.
- **Missing approver:** if a role has no holder or a unit has no head, nothing is started or saved; the sender is told what is missing (HTTP 422) and the decision can be repeated once the organisation is completed.
- **Confidential** letters follow the same steps and stay hidden from anyone without `correspondence:confidential`.

## The twelve types

| Type (code) | Sender | Receiver | Purpose | Steps (responsible, deadline) | Extra conditions |
|---|---|---|---|---|---|
| Administrative memo (`ADMIN_MEMO`) | Staff member or unit | A unit or official | Inform or ask for action inside the administration | 1 Head of the sender's department (3 d) · 2 The recipient (3 d) · Close | Recipient = first internal *To*, else the receiving unit's head |
| Academic (`ACADEMIC`) | Department or college | College, deanship or academic unit | Courses, schedules, programmes | 1 Head of department (3 d) · 2 Dean (3 d) · 3 Head of the receiving unit (3 d) · Close | Needs a receiving unit |
| Financial (`FINANCIAL`) | Any unit | Finance | Payments, advances, budgets | 1 Head of department (3 d) · 2 `FINANCE_MANAGER` (3 d) · 3 `VP_ADMIN_FINANCE` (3 d) · 4 `PRESIDENT` (5 d) · 5 Head of the receiving unit, execution (3 d) · Close | Needs a receiving unit and an amount. Step 3 only above **50,000**, step 4 only above **250,000** (provisional) |
| Between departments (`INTER_UNIT`) | A department | Another department | Coordination | 1 Head of the sending department (3 d) · 2 Head of the receiving department (3 d) · Close | Needs a receiving unit |
| Employee request (`EMPLOYEE_REQUEST`) | Staff member | HR, through the line manager | Leave, secondment, certificates | 1 Line manager (3 d) · 2 `HR_MANAGER` (5 d) · Close | |
| Student request (`STUDENT_REQUEST`) | Student | Student affairs | Statements, grievances, section changes | 1 `STUDENT_AFFAIRS` (5 d) · Close | Off until the university switches it on. A student holds only `correspondence:student-request`: writes and sends only their own, sees only their own (`/mine`, `/history`), never the register |
| Decision (`DECISION`) | Authorised official | Everyone concerned | Official decisions | 1 `LEGAL_ADVISOR` (3 d) · 2 The recipient, notification (2 d) · Close | |
| Circular (`CIRCULAR`) | An official | A group or units | General notices | 1 Line manager (2 d) · 2 Dean (3 d) · Close | |
| Information request or complaint (`INFO_REQUEST`) | Any staff member | The competent unit | Information or a complaint | 1 The recipient or the competent unit's head (5 d) · Close | Needs a recipient or a receiving unit |
| Between college administrations (`COLLEGE_ADMIN`) | An administration of the college | Another administration of the same college | Coordination inside a college | 1 Head of the sending administration (3 d) · 2 Head of the receiving administration (3 d) · Close | Needs a receiving unit **in the sender's college** |
| Between departments of a college (`COLLEGE_DEPT`) | A department of the college | Another department of the same college | Coordination between departments | 1 Head of the sending department (3 d) · 2 Head of the receiving department (3 d) · Close | Needs a receiving unit in the sender's college |
| To the dean or vice dean (`TO_DEAN`) | A member or unit of the college | Vice dean, then dean | A matter put to the college leadership | 1 Line manager (2 d) · 2 Vice dean = deputy of the receiving unit (3 d), **skipped when the college has no vice dean** · 3 Dean = head of the receiving unit (3 d) · Close | The receiving unit is the college itself, which must be the sender's college |

At every step the approver may **approve**, **reject** (reason required) or **return for changes** (reason required). An approver on leave is replaced by their active delegate (existing delegation).

## Who may do what

| Action | Who |
|---|---|
| Create a typed letter | The permission the type names (`correspondence:create`, or `correspondence:student-request` for the student type, only as themselves) |
| Edit, resubmit | The author only, while the letter is Draft or Returned |
| Submit | The author (typed letters); an untyped letter keeps `correspondence:create` plus a named reviewer |
| Approve, reject, return | The person the current step is assigned to (or their delegate) |
| Withdraw | The author, until the letter is approved |
| Close | `records:manage`, any unfinished letter |
| Read the register or the list | `correspondence:read` (confidential: `correspondence:confidential`) |
| Read one letter's history | The author, anyone assigned a step on it, or `correspondence:read` |
| Edit types and workflows | `workflow:manage` |

Roles the standard workflows look for (create them, or edit the workflows): `FINANCE_MANAGER`, `VP_ADMIN_FINANCE`, `PRESIDENT`, `HR_MANAGER`, `STUDENT_AFFAIRS`, `LEGAL_ADVISOR`.

## Decisions to confirm with the university
1. **Financial limits.** 50,000 and 250,000 are placeholders. Set the real ones by editing the `CORR_FINANCIAL` workflow.
2. **Who closes a letter.** Assumed: the last step of the workflow closes it; the author may withdraw; records may close any unfinished letter.
3. **After a return**, does the letter restart from the first step (assumed) or resume at the step that returned it?
4. The **student request** type is created switched off. Switch it on when student affairs is ready to receive them.
5. College types: the sender's department and the receiving unit must share the same top-level unit (the college); the vice dean step is optional (passed over when nobody holds it). Tenants seeded before this change keep the older, mandatory vice dean step until their `CORR_TO_DEAN` workflow is edited (add `"optional": true` to that step).
6. Step names, deadlines and the role codes above are the proposed defaults.

## On screen
Correspondence page: **+ New letter** → choose the type → receiving unit / amount if asked → save as draft → **Send**. Open a letter in the register to see its path and history. A returned letter shows the reviewer's note; **Edit**, then **Send again**. **Withdraw** (author) and **Close** (records) ask for a reason. A student sees **My letters** only.
