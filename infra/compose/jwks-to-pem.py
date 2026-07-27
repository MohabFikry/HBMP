#!/usr/bin/env python3
"""Convert the issuer's first RS256 JWKS key into a PEM public key, for Kong's edge JWT validation.

Kong OSS's `jwt` plugin cannot discover a JWKS: the issuer's public key has to be registered as a consumer
credential in PEM form. That left a manual export step between identity-service and the gateway, and a
missing or stale value does not degrade — Kong either refuses to boot ("invalid key") or rejects every
request at the edge. `up.sh` calls this so a fresh environment wires the two together on its own.

Stdlib only, deliberately: this runs before anything is installed, on whatever Python the host has.
"""
from __future__ import annotations

import base64
import json
import sys
import urllib.request


def b64u_int(value: str) -> int:
    return int.from_bytes(base64.urlsafe_b64decode(value + "=" * (-len(value) % 4)), "big")


def der_len(length: int) -> bytes:
    if length < 0x80:
        return bytes([length])
    encoded = length.to_bytes((length.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(encoded)]) + encoded


def der_int(value: int) -> bytes:
    # (bit_length + 8) // 8 rather than + 7: DER integers are signed, so a value whose top bit is set needs a
    # leading zero byte or it reads as negative — which is every RSA modulus.
    encoded = value.to_bytes((value.bit_length() + 8) // 8, "big")
    return b"\x02" + der_len(len(encoded)) + encoded


def der_seq(*parts: bytes) -> bytes:
    body = b"".join(parts)
    return b"\x30" + der_len(len(body)) + body


def main(url: str) -> int:
    with urllib.request.urlopen(url, timeout=10) as response:  # noqa: S310 - a localhost issuer URL
        keys = json.load(response).get("keys", [])
    signing = next((k for k in keys if k.get("kty") == "RSA" and k.get("alg", "RS256") == "RS256"), None)
    if signing is None:
        print(f"jwks-to-pem: no RS256 RSA key at {url}", file=sys.stderr)
        return 1

    rsa_key = der_seq(der_int(b64u_int(signing["n"])), der_int(b64u_int(signing["e"])))
    algorithm = der_seq(b"\x06\x09\x2a\x86\x48\x86\xf7\x0d\x01\x01\x01", b"\x05\x00")  # rsaEncryption, NULL
    spki = der_seq(algorithm, b"\x03" + der_len(len(rsa_key) + 1) + b"\x00" + rsa_key)

    body = base64.b64encode(spki).decode()
    print("-----BEGIN PUBLIC KEY-----")
    for i in range(0, len(body), 64):
        print(body[i : i + 64])
    print("-----END PUBLIC KEY-----")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8090/.well-known/jwks"))
