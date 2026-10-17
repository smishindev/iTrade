"""Anonymised test fixture from a downloaded IBKR Flex statement (P3.2.02).

    python -I tools/anonymise_flex.py <statement.xml> <fixture.xml> <yyyyMMdd> [<yyyyMMdd> ...]

Keeps the FlexStatements of the given days, drops the conversion rates, replaces every account
number by U0000000 and empties the personal fields (name, addresses, e-mail, country, ...) and
the description of a deposit or withdrawal that holds a digit (a bank account can be in it).
Prints nothing from the statement. Run it with `python -I` and keep it outside the data folder:
the statement is untrusted input. The account becomes U0000000 - a real-looking placeholder - so
that the import's refusal of paper accounts does not stop the tests; that refusal is tested with
DU0000000.
"""

import re
import sys
import xml.etree.ElementTree as ET

PERSONAL = {
    "acctAlias",
    "name",
    "masterName",
    "street",
    "street2",
    "city",
    "state",
    "country",
    "postalCode",
    "streetResidentialAddress",
    "street2ResidentialAddress",
    "cityResidentialAddress",
    "stateResidentialAddress",
    "countryResidentialAddress",
    "postalCodeResidentialAddress",
    "dateOfBirth",
    "primaryEmail",
    "beneficiaryDesignations",
    "accountRepName",
    "accountRepPhone",
    "clientReference",
    "fromAcct",
    "toAcct",
}
ACCOUNT = re.compile(r"\b(?:DU|DF|U|F)\d{5,9}\b")
EMAIL = re.compile(r"[^\s\"@]+@[^\s\"@]+")
PLACEHOLDER = "U0000000"
MONEY_CODES = {"DEP", "WITH"}


def anonymise(source: str, target: str, days: list[str]) -> int:
    root = ET.parse(source).getroot()
    statements = root.find("FlexStatements")
    if statements is None:
        raise SystemExit("not a Flex statement: no FlexStatements")
    for statement in list(statements):
        if statement.get("fromDate") not in days:
            statements.remove(statement)
    statements.set("count", str(len(statements)))
    for statement in statements:
        rates = statement.find("ConversionRates")
        if rates is not None:
            for rate in list(rates):
                rates.remove(rate)
    # A deposit's description can carry a bank account; IBKR's generic ones have no digit. IBKR
    # repeats it in other sections (FxTransaction, FxLot), so the same text is emptied everywhere.
    private = set()
    for element in root.iter():
        money_in_or_out = (
            element.get("type") == "Deposits/Withdrawals"
            or element.get("activityCode") in MONEY_CODES
        )
        for key in ("description", "activityDescription"):
            if money_in_or_out and re.search(r"\d", element.get(key, "")):
                private.add(element.get(key))
    for element in root.iter():
        for key, value in list(element.attrib.items()):
            if value in private or key in PERSONAL:
                element.set(key, "")
            elif key == "accountId" and value not in ("", "-"):
                element.set(key, PLACEHOLDER)
            else:
                element.set(key, EMAIL.sub("", ACCOUNT.sub(PLACEHOLDER, value)))
    ET.ElementTree(root).write(target, encoding="utf-8", xml_declaration=False)
    with open(target, encoding="utf-8") as written:
        text = written.read()
    accounts = re.findall(r"\b[UF]\d{7,9}\b", text)
    if re.search(r"\b(?:DU|DF)\d", text) or "@" in text or len(accounts) != text.count(PLACEHOLDER):
        raise SystemExit(f"{target} still holds an account number or an e-mail - do not commit it")
    return len(statements)


if __name__ == "__main__":
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    kept = anonymise(sys.argv[1], sys.argv[2], sys.argv[3:])
    print(f"{kept} day(s) written to {sys.argv[2]}")
