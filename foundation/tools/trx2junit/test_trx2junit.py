"""Self-contained tests for trx2junit. Run: python test_trx2junit.py"""
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import trx2junit  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURE = os.path.join(HERE, "fixture.trx")


def test_counts_and_shape():
    tree, total, failures, skipped = trx2junit.convert([FIXTURE], "device-test")
    assert (total, failures, skipped) == (3, 1, 1), (total, failures, skipped)

    suite = tree.find("testsuite")
    assert suite.get("name") == "device-test"
    assert suite.get("tests") == "3"
    assert suite.get("failures") == "1"
    assert suite.get("skipped") == "1"
    assert abs(float(suite.get("time")) - 2.623) < 0.001, suite.get("time")

    cases = suite.findall("testcase")
    assert len(cases) == 3
    assert cases[1].find("failure").get("message") == "Assert.Equal() Failure"
    assert "Vec0AndFts5AreAvailableOnDevice" in cases[1].find("failure").text
    assert cases[1].find("system-out").text == "opening device-tests.db"
    assert cases[2].find("skipped") is not None


def test_missing_files_exit_code():
    with tempfile.TemporaryDirectory() as tmp:
        rc = trx2junit.main(["--suite", "x", "--out", os.path.join(tmp, "o.xml"), os.path.join(tmp, "nope.trx")])
    assert rc == 1


def test_writes_parsable_xml():
    with tempfile.TemporaryDirectory() as tmp:
        out = os.path.join(tmp, "junit", "device.xml")
        rc = trx2junit.main(["--suite", "device-test", "--out", out, FIXTURE])
        assert rc == 0
        assert ET.parse(out).getroot().tag == "testsuites"


if __name__ == "__main__":
    failed = 0
    for name, fn in sorted((n, f) for n, f in globals().items() if n.startswith("test_")):
        try:
            fn()
            print("PASS %s" % name)
        except AssertionError as ex:
            failed += 1
            print("FAIL %s: %s" % (name, ex))
    print("OK: 3 tests passed" if failed == 0 else "FAILED: %d" % failed)
    raise SystemExit(1 if failed else 0)
