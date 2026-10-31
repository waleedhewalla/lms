# Internal correspondence: types and workflows

Every internal letter has a **type**. The type says who may start it, what it must contain, and which workflow it follows from creation to closure. The catalogue and the workflows are data: the university edits them (`PATCH /api/correspondence/types/{id}`, `POST /api/workflows/definitions/{id}/new-version`) without a release. `POST /api/correspondence/types/seed-defaults` creates the standard fifteen and their workflows; anything the university already has is left alone.

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
- **Closing:** every workflow ends with a closing step done by the **head of the college's administrative affairs unit** (a unit with code `ADMIN_AFFAIRS` inside the sender's college; for a student, the college of the student's department, or of the receiving unit). Create that unit and give it a head in every college; without it the last approval is refused with a message naming what is missing, and can be repeated once it exists. Records (`records:manage`) can still close any unfinished letter.
- **Confidential** letters follow the same steps and stay hidden from anyone without `correspondence:confidential`.

## The fifteen types

| Type (code) | Sender | Receiver | Purpose | Steps (responsible, deadline) | Extra conditions |
|---|---|---|---|---|---|
| Administrative memo (`ADMIN_MEMO`) | Staff member or unit | A unit or official | Inform or ask for action inside the administration | 1 Head of the sender's department (3 d) · 2 The recipient (3 d) · Close | Recipient = first internal *To*, else the receiving unit's head |
| Academic (`ACADEMIC`) | Department or college | College, deanship or academic unit | Courses, schedules, programmes | 1 Head of department (3 d) · 2 Dean (3 d) · 3 Head of the receiving unit (3 d) · Close | Needs a receiving unit |
| Financial (`FINANCIAL`) | Any unit | Finance | Payments, advances, budgets | 1 Head of department (3 d) · 2 `FINANCE_MANAGER` (3 d) · 3 `VP_ADMIN_FINANCE` (3 d) · 4 `PRESIDENT` (5 d) · 5 Head of the receiving unit, execution (3 d) · Close | Needs a receiving unit and an amount. Step 3 only above **50,000**, step 4 only above **250,000** (provisional) |
| Between departments (`INTER_UNIT`) | A department | Another department | Coordination | 1 Head of the sending department (3 d) · 2 Head of the receiving department (3 d) · Close | Needs a receiving unit |
| Employee request (`EMPLOYEE_REQUEST`) | Staff member | HR, through the line manager | Leave, secondment, certificates | 1 Line manager (3 d) · 2 `HR_MANAGER` (5 d) · Close | |
| Student services (`STUDENT_STATEMENT`, `STUDENT_GRIEVANCE`, `STUDENT_SECTION_CHANGE`) | Student | Student affairs | One type per service, chosen by the student from a list | 1 `STUDENT_AFFAIRS` (3 d statement, 7 d grievance, 5 d section change) · Close | On by default. A student holds only `correspondence:student-request`: sees the list of student services only, writes and sends only their own letters, sees only their own (`/mine`, `/history`), never the register. Add a service with `POST /api/correspondence/types` (permission `correspondence:student-request`) |
| Student request, generic (`STUDENT_REQUEST`) | Student | Student affairs | Anything not in the list | 1 `STUDENT_AFFAIRS` (5 d) · Close | Off until the university switches it on |
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

## Decisions taken by the university
1. **Financial limits** are configurable **per approval level**: `GET/PUT /api/correspondence/financial-limits` (and the "Financial approval limits" panel on the Correspondence page, for `workflow:manage`) set, for each level, the amount above which that level also approves a financial letter; empty = always approves. Each change is a new version of the `CORR_FINANCIAL` workflow. The shipped values (50,000 for the vice president, 250,000 for the president) are placeholders until the university sets its own.
2. **Who closes:** the college's administrative affairs unit, as the last step (see "Closing" above).
3. **After a return,** the letter restarts from the first step.
4. **Student services** are chosen from a list: each service is a type of its own and is on by default.

Still to confirm: the step names, deadlines and role codes above are the proposed defaults; tenants seeded before this change keep their older workflows until `seed-defaults` is run for new types and the closing step is added with `new-version`.

## On screen
Correspondence page: **+ New letter** → choose the type → receiving unit / amount if asked → save as draft → **Send**. Open a letter in the register to see its path and history. A returned letter shows the reviewer's note; **Edit**, then **Send again**. **Withdraw** (author) and **Close** (records) ask for a reason. A student sees **My letters** only.
