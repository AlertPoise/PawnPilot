"""固定 loopback 诊断客户端；不重试动作，不启动 broker。"""
import argparse
import json
import re
from pathlib import Path
import urllib.error
import urllib.request

PORT = 18765
MAX_RESPONSE = 65536


class ResponseError(ValueError):
    """The endpoint returned an invalid or oversized protocol response."""


def read_response(response):
    if response.headers.get_content_type() != "application/json":
        raise ResponseError("response_json_required")
    raw = response.read(MAX_RESPONSE + 1)
    if len(raw) > MAX_RESPONSE:
        raise ResponseError("response_too_large")
    try:
        value = json.loads(raw, parse_constant=lambda _: (_ for _ in ()).throw(ValueError("nonfinite")))
    except (ValueError, UnicodeError) as exc:
        raise ResponseError("response_invalid_json") from exc
    if not isinstance(value, dict):
        raise ResponseError("response_object_required")
    return value


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def request(config, path, body=None):
    raw = None if body is None else json.dumps(body).encode("utf-8")
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}", data=raw,
                                 headers={"Authorization": "Bearer " + config["token"],
                                          "Content-Type": "application/json"})
    # 忽略系统代理，避免 localhost 请求和密钥送往代理服务器。
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    try:
        with opener.open(req, timeout=4) as response:
            return response.status, read_response(response)
    except urllib.error.HTTPError as exc:
        return exc.code, read_response(exc)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", default="artifacts/session.json")
    parser.add_argument("action", choices=["state", "result", "observe", "close_menu", "reconcile"])
    parser.add_argument("--expected-version")
    parser.add_argument("--id")
    parser.add_argument("--broker-epoch")
    parser.add_argument("--world-epoch")
    parser.add_argument("--revision", type=int)
    parser.add_argument("--operator-note")
    parser.add_argument("--confirm-no-replay", action="store_true")
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text(encoding="utf-8"))
    if args.action == "state":
        status, result = request(config, "/state")
    elif args.action == "result":
        if not args.id or not re.fullmatch(r"[A-Za-z0-9_-]{1,128}", args.id):
            parser.error("result requires --id containing letters, digits, dash or underscore")
        status, result = request(config, "/results/" + args.id)
    elif args.action == "reconcile":
        if (not all([args.id, args.broker_epoch, args.world_epoch, args.expected_version, args.operator_note])
                or args.revision is None or not args.confirm_no_replay):
            parser.error("manual reconciliation requires reviewed current version fields, --operator-note and --confirm-no-replay")
        status, result = request(config, "/reconcile", {
            "schema_version": 1, "id": args.id, "broker_epoch": args.broker_epoch,
            "world_epoch": args.world_epoch, "revision": args.revision,
            "expected_version": args.expected_version, "operator_note": args.operator_note,
            "confirm_no_replay": True})
    else:
        if not all([args.id, args.broker_epoch, args.world_epoch, args.expected_version]) or args.revision is None:
            parser.error("commands require --id, --broker-epoch, --world-epoch, --revision and --expected-version from a reviewed snapshot")
        status, result = request(config, "/commands", {
            "schema_version": 1, "id": args.id, "action": args.action,
            "broker_epoch": args.broker_epoch, "expected_world_epoch": args.world_epoch,
            "expected_revision": args.revision, "expected_version": args.expected_version})
    print(json.dumps({"http_status": status, "body": result}, ensure_ascii=False, indent=2))
    raise SystemExit(0 if status == 200 else 1)


if __name__ == "__main__":
    main()
