"""Export the DealOS solution through the Web API and unpack it into solutions/DealOS.

Uses the dv.py browser sign-in, because PAC CLI's own sign-in is blocked by the tenant's security defaults.
Unpacking runs PAC offline (no sign-in needed).

Usage: python3 tools/export_solution.py
"""
import base64
import os
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import dv  # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAC = os.path.expanduser("~/.dotnet/tools/pac")
DOTNET_ROOT = "/opt/homebrew/opt/dotnet/libexec"


def main():
    s, b = dv.request("POST", "ExportSolution", {"SolutionName": "DealOS", "Managed": False}, timeout=600)
    if s >= 300:
        sys.exit(f"export failed: {s} {str(b)[:800]}")
    zip_path = os.path.join(tempfile.mkdtemp(), "DealOS.zip")
    with open(zip_path, "wb") as f:
        f.write(base64.b64decode(b["ExportSolutionFile"]))
    print(f"exported {os.path.getsize(zip_path)} bytes")
    env = dict(os.environ, DOTNET_ROOT=DOTNET_ROOT)
    subprocess.run([PAC, "solution", "unpack", "--zipfile", zip_path, "--folder", os.path.join(ROOT, "solutions", "DealOS"),
                    "--packagetype", "Unmanaged", "--allowDelete", "true"], check=True, env=env)


if __name__ == "__main__":
    main()
