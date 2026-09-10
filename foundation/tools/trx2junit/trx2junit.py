#!/usr/bin/env python3
"""Convert one or more VSTest TRX files into a single JUnit XML document.

Usage: trx2junit.py --suite NAME --out PATH TRX [TRX ...]

Only the elements DeviceRunners actually emits are handled: UnitTestResult rows with an
outcome of Passed / Failed / NotExecuted, an optional Output/ErrorInfo block, and a duration.
"""
import argparse
import os
import sys
import xml.etree.ElementTree as ET

TRX_NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"


def _duration_seconds(value):
    # TRX durations look like "00:00:01.2345678"; anything unparseable counts as zero.
    if not value:
        return 0.0
    try:
        hours, minutes, seconds = value.split(":")
        return int(hours) * 3600 + int(minutes) * 60 + float(seconds)
    except (ValueError, AttributeError):
        return 0.0


def _results(path):
    root = ET.parse(path).getroot()
    for result in root.iter(TRX_NS + "UnitTestResult"):
        output = result.find(TRX_NS + "Output")
        message = ""
        stack = ""
        stdout = ""
        if output is not None:
            info = output.find(TRX_NS + "ErrorInfo")
            if info is not None:
                message = (info.findtext(TRX_NS + "Message") or "").strip()
                stack = (info.findtext(TRX_NS + "StackTrace") or "").strip()
            stdout = (output.findtext(TRX_NS + "StdOut") or "").strip()
        yield {
            "name": result.get("testName") or "unnamed",
            "outcome": result.get("outcome") or "Failed",
            "seconds": _duration_seconds(result.get("duration")),
            "message": message,
            "stack": stack,
            "stdout": stdout,
        }


def convert(trx_paths, suite_name):
    cases = [case for path in trx_paths for case in _results(path)]

    failures = sum(1 for c in cases if c["outcome"] == "Failed")
    skipped = sum(1 for c in cases if c["outcome"] == "NotExecuted")

    suites = ET.Element("testsuites")
    suite = ET.SubElement(
        suites,
        "testsuite",
        name=suite_name,
        tests=str(len(cases)),
        failures=str(failures),
        errors="0",
        skipped=str(skipped),
        time="%.3f" % sum(c["seconds"] for c in cases),
    )

    for case in cases:
        node = ET.SubElement(
            suite,
            "testcase",
            classname=case["name"].rsplit(".", 1)[0] if "." in case["name"] else suite_name,
            name=case["name"],
            time="%.3f" % case["seconds"],
        )
        if case["outcome"] == "Failed":
            failure = ET.SubElement(node, "failure", message=case["message"] or "test failed", type="AssertionError")
            failure.text = case["stack"] or case["message"]
        elif case["outcome"] == "NotExecuted":
            ET.SubElement(node, "skipped")
        if case["stdout"]:
            ET.SubElement(node, "system-out").text = case["stdout"]

    return suites, len(cases), failures, skipped


def main(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--suite", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("trx", nargs="+")
    args = parser.parse_args(argv)

    existing = [p for p in args.trx if os.path.isfile(p)]
    if not existing:
        print("trx2junit: no TRX files found in %r" % (args.trx,), file=sys.stderr)
        return 1

    tree, total, failures, skipped = convert(existing, args.suite)
    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    ET.ElementTree(tree).write(args.out, encoding="utf-8", xml_declaration=True)
    print("trx2junit: %d tests, %d failed, %d skipped -> %s" % (total, failures, skipped, args.out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
