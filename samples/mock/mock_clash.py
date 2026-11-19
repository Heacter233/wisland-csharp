"""
本地 mock Clash 外部控制器，用于验证 ClashPlugin 的读写逻辑（不需要真的跑 Clash）。

用法：
    python mock_clash.py 19090
然后在插件设置页把端口改成 19090、密钥留空即可。

实现的接口（与 mihomo / Clash 一致）：
    GET   /version
    GET   /configs
    PATCH /configs        {"mode": "rule|global|direct"}
    GET   /proxies
    PUT   /proxies/<组名>  {"name": "节点"}
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

STATE = {
    "version": "v1.18.0-mock",
    "mode": "rule",
    "proxies": {
        "GLOBAL": {"type": "Selector", "now": "DIRECT", "all": ["DIRECT", "REJECT", "节点A", "节点B"]},
        "🚀 节点选择": {"type": "Selector", "now": "自动选择", "all": ["自动选择", "节点A", "节点B", "DIRECT"]},
        "自动选择": {"type": "URLTest", "now": "节点A", "all": ["节点A", "节点B"]},
        "🎯 全球直连": {"type": "Selector", "now": "DIRECT", "all": ["DIRECT", "REJECT"]},
        "节点A": {"type": "Shadowsocks", "now": "", "all": []},
    },
}


class Handler(BaseHTTPRequestHandler):
    def _send(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _auth_ok(self):
        # mock 不校验密钥，方便调试
        return True

    def do_GET(self):
        if self.path.startswith("/version"):
            return self._send({"version": STATE["version"], "meta": True})
        if self.path.startswith("/configs"):
            return self._send({"mode": STATE["mode"], "port": 7897, "allow-lan": False})
        if self.path.startswith("/proxies"):
            return self._send({"proxies": STATE["proxies"]})
        return self._send({"error": "not found"}, 404)

    def do_PATCH(self):
        if self.path.startswith("/configs"):
            n = int(self.headers.get("Content-Length", 0))
            data = json.loads(self.rfile.read(n) or b"{}")
            if "mode" in data:
                STATE["mode"] = data["mode"]
            return self._send({}, 204)
        return self._send({"error": "not found"}, 404)

    def do_PUT(self):
        if self.path.startswith("/proxies/"):
            from urllib.parse import unquote
            group = unquote(self.path[len("/proxies/"):])
            n = int(self.headers.get("Content-Length", 0))
            data = json.loads(self.rfile.read(n) or b"{}")
            node = data.get("name")
            if group not in STATE["proxies"]:
                return self._send({"error": "group not found"}, 404)
            if node not in STATE["proxies"][group].get("all", []):
                return self._send({"error": "node not in group"}, 400)
            STATE["proxies"][group]["now"] = node
            print(f"[mock] {group} -> {node}")
            return self._send({}, 204)
        return self._send({"error": "not found"}, 404)

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 19090
    print(f"mock clash controller on http://127.0.0.1:{port}")
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
