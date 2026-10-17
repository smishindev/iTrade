# Israeli tax rules — first-hand sources (P4.0.01)

_Collected 17 October 2026 for the owner: an individual, Israeli tax resident living in Israel, not a new immigrant or
returning resident (no section 14 exemptions), an employee taxed at source, not a substantial shareholder, not trading
as a business, holding US-listed ETFs and dollar cash at Interactive Brokers, which withholds no Israeli tax._

**Not tax advice and not checked by an accountant** (owner's decision of 12 October 2026; the check is P4.3.01). Every
rule below names where it was read. Confidence: **high** — the text says so; **medium** — the text plus a reading of
it; **low** — no first-hand text, the rule is not used by the code (see Open questions). A rule is replaced only with
a newer source or an accountant's answer, with its date.

## Sources read

| Key | Source | Version read |
|---|---|---|
| ORD | Income Tax Ordinance (פקודת מס הכנסה [נוסח חדש]) — https://www.nevo.co.il/law_html/law01/255_001.htm; cross-read in the Wikisource consolidation https://he.wikisource.org/wiki/פקודת_מס_הכנסה | Nevo "updated to 27-09-2026"; Wikisource revision 2026-09-06 (up to amendment 285). A consolidation, not the gazette |
| CONV | כללי מס הכנסה (המרה לשקלים חדשים של סכומים שמקורם מחוץ לישראל), תשס"ד-2003 — https://www.nevo.co.il/law_html/law01/999_233.htm | ק"ת 6280, 28.12.2003; no amendment found |
| REG | תקנות מס הכנסה (חישוב רווח הון במכירת נייר ערך הנסחר בבורסה, מילווה המדינה או יחידה בקרן נאמנות), תשס"ג-2002 — https://www.nevo.co.il/law_html/law01/999_087.htm | Nevo 15-07-2025 |
| ADV | תקנות מס הכנסה (מקדמה על הכנסות חוץ), 2004 — https://www.nevo.co.il/law_html/law01/999_299.htm | as on Nevo, 2026 |
| EXEMPT | תקנות מס הכנסה (פטור מהגשת דין וחשבון), 1988 — https://www.nevo.co.il/law_html/law00/72579.htm | Nevo 16-02-2026 |
| C10 | Tax Authority professional circular 10/2025 "קיזוז הפסדי הון" — https://www.gov.il/BlobFolder/policy/professional-directives-271125-1/he/IncomeTax_professional-directives-271125-1.pdf | 27.11.2025 |
| I5 | Tax Authority instruction 5/2025 (additional tax on capital income) — https://www.gov.il/BlobFolder/policy/inst-05-2025/he/IncomeTax_inst-05-2025.pdf | 25.2.2025 |
| WB | Monthly withholding booklet 2026 — https://www.gov.il/BlobFolder/generalpage/income-tax-monthly-deductions-booklet/he/generalInformation_income-tax-monthly-deductions-booklet_monthly-deductions-booklet-2026.pdf | republished after the budget law of 31.3.2026 |
| F | Annual-return forms of tax year 2025 (1301, 1322, 1324, 1325, 1326, 135) — https://www.gov.il/BlobFolder/service/reporting-and-payment-2025-annual-tax-report-for-individuals/he/Service_Pages_Income_tax_annual-report-2026_<form>-2025(-ACC).pdf | revised 12.2025 / 1.2026; **the 2026 forms are not published yet** |
| GUIDE | Tax Authority guide "דע זכויותיך וחובותיך" 2025 — https://www.gov.il/BlobFolder/generalpage/income-tax-guide-knowyourright/he/Guides_IncomeTax_da-2025.pdf | 2025 |
| TREATY | Israel–US income tax convention (1975, protocols 1980 and 1993) — https://www.irs.gov/pub/irs-trty/israel.pdf; IRS Tax Treaty Table 1 — https://www.irs.gov/pub/irs-lbi/tax-treaty-table-1.pdf | Table 1 rev. May 2023 |
| P515 | IRS Publication 515 (withholding on nonresident aliens) — https://www.irs.gov/publications/p515 | 2026 |
| MOSES | ע"א 3555/15 מוזס נ' פקיד שומה, 12.9.2016; דנ"א 7517/16, 3.8.2017 (further hearing refused) — read from copies hosted by RSM (secondary host) | — |

## Rules

### R1. Rate on a real capital gain — high
An individual pays tax on the **real** capital gain at the marginal rate, **capped at 25%**, the gain counted as the
top slice of taxable income; 30% only for a substantial shareholder (10% of a means of control, at the sale or in the
12 months before). The form lists 25% for the real gain on foreign securities. 15% is only for non-indexed bonds.
— ORD s.91(b)(1)–(2), s.88 "בעל מניות מהותי"; F 1325. _"יחיד יהיה חייב במס על רווח הון ריאלי כאמור בסעיף 121, בשיעור
שלא יעלה על 25%, ויראו את רווח ההון כשלב הגבוה ביותר בסולם הכנסתו החייבת"._
**Code:** the rate is 25% (a parameter). "Capped at the marginal rate": an employee whose top bracket is below 25%
pays less — the platform does not know the owner's other income, so it states the 25% figure as the ceiling.

### R2. Real gain of a security bought in dollars — high
- Capital gain = proceeds − remaining original price (proceeds net of selling costs; the form nets commissions into
  cost and proceeds). Inflationary amount = the part of the gain by which the adjusted price exceeds the original
  price. Real gain = capital gain − inflationary amount. — ORD s.88; F 1325.
- For a security held by an individual and **denominated in foreign currency, the exchange rate is the index** —
  mandatory, no choice (the choice exists only for an asset bought while non-resident). — ORD s.88 "מדד"; C10. _"לענין
  נייר ערך בידי יחיד, הנקוב במטבע חוץ או שערכו צמוד למטבע חוץ, יראו את שער המטבע כמדד"._
- In shekels: cost₪ = cost$ × rate(purchase), proceeds₪ = proceeds$ × rate(sale) (R3); adjusted cost₪ = cost$ ×
  rate(sale); inflationary amount = adjusted cost₪ − cost₪.
- **Shekel strengthened** (inflationary amount negative): it is taken as 0 — the whole nominal shekel gain is real
  and taxed. — C10 appendix, example 3; MOSES.
- **Nominal shekel loss**: the part caused by the exchange rate is not offsettable; offsettable loss = adjusted
  cost₪ − proceeds₪. — C10 example 4 (nominal loss ₪640, of it ₪300 exchange rate, ₪340 allowed); ORD s.92(a)(1);
  MOSES. _"יש לנטרל את הסכום האינפלציוני השלילי שאילו היה רווח לא היה חייב במס"._ (Our reading, not stated: if that
  is negative, the offsettable loss is 0.)
- **Dollar loss, shekel weakened**: a nominal shekel gain smaller than the exchange-rate effect is all exempt
  inflationary amount; a nominal shekel loss is offsettable in full. — C10 example 2.

**Code** (r_b, r_s the rates of purchase and sale): nominal₪ = proceeds$ × r_s − cost$ × r_b; inflationary amount =
cost$ × (r_s − r_b).
- nominal₪ > 0: real gain = nominal₪ − the inflationary amount, the latter taken between 0 and nominal₪ — the shekel
  weakened: the dollar gain at the sale rate, or 0 if the weakening is larger than the gain; it strengthened: the
  whole nominal₪.
- nominal₪ < 0, the shekel weakened (r_s ≥ r_b): an offsettable loss of −nominal₪, in full.
- nominal₪ < 0, the shekel strengthened (r_s < r_b): an offsettable loss of max(0, cost$ × r_s − proceeds$ × r_s).
Hand-worked tests reproduce C10's examples 2–4.

### R3. Which exchange rate — high
The Bank of Israel **representative rate** published on the day, or the last one published before it (CONV r.1).
- Purchase price: the rate of **the day of purchase or of payment, whichever is earlier** (r.7(1)) — for a trade
  that settles later, the trade date.
- Sale proceeds: **the day of sale or of receipt, whichever is earlier** (r.7(2)) — the trade date.
- Dividends, interest (passive income): the day **paid or received** (r.3(א)).
- Foreign tax: the day it was paid, when paid within the tax year; tax paid later (within 24 months) at the rate of
  the end of the tax year (r.6).
- Losses carried forward are kept in shekels (r.8).
— CONV; cited by F 1324 note 1. _"הכנסה פסיבית, תתורגם לשקלים חדשים לפי שערו היציג של המטבע, ביום שבו שולמה ההכנסה
או שהתקבלה"._
**Code:** the platform's `FxRates.OnAsync(date)` gives exactly "the day, or the last published before it". The New
York trade date is the date used; a provisional rate is never good enough for a tax figure.

### R4. Which lots are sold — medium
Identical securities bought at different times are sold **in the order bought, first to last** (FIFO), unless the
seller proves the purchase date and price of a specific one. — REG reg. 5. _"יראו אותם… כנמכרים על פי סדר רכישתם,
מהראשון עד האחרון"._ The form asks for each sale "לפי הסדר הכרונולוגי". Caveat: REG excludes "נייר ערך זר" from its
"נייר ערך", while its "ניירות ערך זהים" includes foreign ones held by one portfolio manager — no first-hand text
applies reg. 5 to ETFs held directly at a foreign broker (open question O2).
**Code:** FIFO, as the books already do (`LotMatching.Fifo`).

### R5. Offsetting and carrying losses — high
- Same year: a capital loss offsets real gain one for one, then taxable inflationary amount at 1 : 3.5; a loss counts
  only where a gain on the same asset would have been taxed. — ORD s.92(a)(1).
- A loss on an asset **abroad** goes first against gains abroad (s.92(a)(3)), and — per C10 §3.8 (Grinfeld) — first
  against interest and dividends earned abroad.
- A current-year loss on securities also offsets, **in the same year only**, interest or dividends on the same
  security, or on other securities taxed at no more than 25% (the additional tax ignored for that test); not interest
  on deposits or savings, not mutual-fund distributions. — ORD s.92(a)(4); C10 §§3.3.2, 3.7.
- Carried forward: to the following years one after another, **against capital gains only**, at nominal value, no
  time limit, **only if a return was filed for the year of the loss** (and for the year of the offset); every offset
  available earlier must have been used; no carry-back. — ORD s.92(b); C10 §§2.1–2.2, 4.2–4.3.
- Order: the taxpayer chooses, within s.92 and "abroad first". — C10 §6.1.

### R6. Additional tax on high income — high
3% on taxable income above **₪721,560** (2026; indexation frozen 2025–2027), and since 1.1.2025 another **2% on
income from capital sources** above the same threshold, tested on capital income alone — 5% in all on that part. The
inflationary amount is not in the base. — ORD s.121B(a), (a1), (e); I5; WB. _"על חלק ההכנסה החייבת ממקור הוני העולה
על התקרה יוטל מס בשיעור של 5%"._
**Code:** a parameter with its threshold; the platform does not know the owner's other income, so it says when the
portfolio alone comes near the threshold, and otherwise leaves the additional tax out by name.

### R7. Dividends from a US fund, and the US withholding — high
- An individual pays **25%** on a dividend (30% for a substantial shareholder). — ORD s.125B; F 1324 ("מדיבידנד…
  מס בשיעור 25%").
- The treaty caps US tax on dividends to an Israeli resident at **25%**, also for a regulated investment company
  (an ETF). — TREATY Art. 12(2)(a), 1993 protocol Art. VII (new 12(3)(a)); Table 1. Without a valid **W-8BEN** the US
  withholds **30%**. — P515.
- Foreign tax is credited against the Israeli tax on the same income, never more than that tax (per source, special
  rate); excess carries forward 5 years, indexed, against the same source; the foreign tax must be paid within 24
  months of the end of the year. — ORD ss.199–210, in particular 200(a), 204(b), 205A(a), 207B; GUIDE.
**The owner's account:** a W-8BEN is on file at IBKR (Profile Information → Tax Forms, seen by the owner on
17 October 2026); a W-8BEN generally stays in effect to the end of the third calendar year after signing (P515). The
first dividend's withholding line confirms the 25%.
**Code:** 25% withheld → nothing more due in Israel; 30% withheld → nothing more due, and the 5% is excess credit (not
refunded by Israel; reclaimed in the US, or carried forward). Shekels at the rate of the payment day (R3).

### R8. Interest on the dollar cash at IBKR — medium
At most 25% on interest; 15% when the asset is not linked; for an asset in foreign currency the "index" is the
exchange rate — so dollar cash counts as linked: **25%**. — ORD s.125C(b), (c)(1) and its "מדד"; GUIDE; F 1324 (separate
lines at 15% and 25%). Treaty cap on US tax on interest: 17.5%. — TREATY Art. 13(2).
**Code:** 25%; what the US withholds on IBKR's interest is O4.

### R9. Semi-annual report and advance on gains — high (form: low)
With no Israeli withholding, a seller who must file an annual return reports on **31 July and 31 January** the sales
of the six months before and pays an advance equal to the tax on that gain; a foreign exchange is a "בורסה". —
ORD s.91(d)(1), (2c)(a)–(b), (6); GUIDE. _"יוגש הדוח … ביום 31 ביולי וביום 31 בינואר … בשל מכירה של ניירות ערך בששת
החודשים שקדמו לחודש שבו חל יום הדיווח"._ The annual form 1322 asks for separate 1322/1325 per half-year "בתשלום מקדמה
חצי-שנתית". The format of the half-year report and netting losses inside the half-year: O5.

### R10. Annual advance on foreign income — high
Foreign dividends and gains on foreign securities without full withholding are "foreign income"; an advance is due
by **30 April of the following year**: income × rate − foreign tax paid on it, no other offsets; a report of the
computation is attached; payment vouchers 351/352. — ADV regs 1–6; GUIDE.

### R11. Annual return — high (application: medium)
Every resident individual aged 18+ files, unless exempt (ORD s.131(a)(1)); deadline **30 April 2027** for 2026 (s.132(a));
extensions are announced yearly (none found yet for 2026). The exemption for "income from a security" needs the full
tax withheld — **not the case at IBKR**; "foreign income" is exempt only below a cap (₪375,000 in 2025) and with the
advance of R10 paid; foreign assets of ₪2,086,000 (2025) disqualify. — EXEMPT regs 2–3, schedules ד, ו; GUIDE.
Forms: **1301** (form 135 is not for foreign income), **1322** (נספח ג, gains on securities), **1325** (נספח ג1, every
sale, separate per rate, half-year and withholding), **1324** (נספח ד, foreign income and the foreign tax credit);
1326 is for securities bought before 2006 — not this case. — F.
Our reading (medium): with sales at IBKR the owner files an annual return.

### R12. Keeping records — low
The 7-year rule binds business books only (bookkeeping instructions s.25(c)); no first-hand retention rule for an
individual without a business was found. The assessing officer may examine a return within 4 years from the end of
the year it was filed in (ORD s.145(a)(2)). Foreign tax is credited only against attached certificates (F 1324).
**Code:** the platform keeps every imported statement byte for byte and never deletes one.

## Open questions — the code refuses or names these cases, it does not guess

| | Question | Why it matters | What the code does |
|---|---|---|---|
| O1 | Is all of a US ETF's distribution a "dividend"? A RIC can pay capital-gain distributions and return of capital | another rate or a change of cost | already: the import refuses a distribution not described "(Ordinary Dividend)" |
| O2 | Does REG reg. 5 (FIFO) formally bind ETFs held directly at a foreign broker? | the lot rule | FIFO by assumption (R4), named in every result |
| O3 | Exchange-rate differences on dollar cash held by an individual (ORD s.9(13) exemption for linkage differences on a deposit, or a conversion as the sale of an asset?) | a tax on converting shekels and dollars | a conversion is recorded, its shekel effect is named as untaxed **by assumption**, flagged in every result |
| O4 | What does the US withhold on IBKR's credit interest (portfolio interest exemption, or 17.5%)? | the credit on interest | the withheld amount comes from the statement, never assumed; 1042-S box 6 shows the exemption |
| O5 | The format of the half-year report, and netting losses inside the half-year | R9 | the amount is computed per half-year, its form is not produced |
| O6 | 2026 indexed figures of the filing exemptions, and any filing extension for 2026 | R11 | 2025 figures with their year, labelled |
| O7 | A retention period for an individual's records | R12 | everything is kept |
| O8 | When the marginal rate is below 25% (R1), or the additional tax applies (R6) | the owner's other income is not in the platform | 25% shown as the ceiling; the additional tax named, not computed |
