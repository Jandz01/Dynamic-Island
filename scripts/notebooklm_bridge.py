"""
NotebookLM Direct API Bridge for Dynamic Island
Communicates directly with Google NotebookLM via internal RPCs using local browser authentication.
Supports multi-account routing (authuser 0, 1, 2, 3...) so ANY Google account signed into the browser can add sources.
"""

import sys
import os
import re
import json
import argparse
import asyncio
import tempfile
from pathlib import Path

# Ensure UTF-8 output on Windows console
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    except Exception:
        pass

from notebooklm import NotebookLMClient

CACHE_FILE = Path.home() / ".notebooklm" / "island_cache.json"

def extract_notebook_id_and_authuser(url_or_id: str) -> tuple[str, int | None]:
    url_or_id = url_or_id.strip()
    authuser = None

    # Check for /u/1/ or /u/0/ in Google URLs
    u_match = re.search(r"/u/(\d+)/", url_or_id)
    if u_match:
        authuser = int(u_match.group(1))
    else:
        # Check query param ?authuser=1
        au_match = re.search(r"[?&]authuser=(\d+)", url_or_id)
        if au_match:
            authuser = int(au_match.group(1))

    nb_id = url_or_id
    if "/notebook/" in url_or_id:
        match = re.search(r"/notebook/([a-f0-9\-]+)", url_or_id, re.IGNORECASE)
        if match:
            nb_id = match.group(1)
    return nb_id, authuser

def get_cached_auth(notebook_id: str) -> tuple[str | None, int | None]:
    try:
        if CACHE_FILE.exists():
            data = json.loads(CACHE_FILE.read_text(encoding="utf-8"))
            prof = data.get("profiles", {}).get(notebook_id)
            au = data.get("authusers", {}).get(notebook_id)
            return prof, au
    except Exception:
        pass
    return None, None

def set_cached_auth(notebook_id: str, profile_name: str, authuser: int, notebook_title: str = ""):
    try:
        CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        data = {}
        if CACHE_FILE.exists():
            try:
                data = json.loads(CACHE_FILE.read_text(encoding="utf-8"))
            except Exception:
                data = {}
        if "profiles" not in data:
            data["profiles"] = {}
        if "authusers" not in data:
            data["authusers"] = {}
        if "titles" not in data:
            data["titles"] = {}
        data["profiles"][notebook_id] = profile_name
        data["authusers"][notebook_id] = authuser
        if notebook_title:
            data["titles"][notebook_id] = notebook_title
        CACHE_FILE.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    except Exception:
        pass

def get_available_profiles() -> list[str]:
    profiles_dir = Path.home() / ".notebooklm" / "profiles"
    if not profiles_dir.exists():
        return []
    found = []
    for d in profiles_dir.iterdir():
        if d.is_dir() and (d / "storage_state.json").exists():
            found.append(d.name)

    # Prioritize profiles that have valid sessions
    preferred = ["default", "chrome_auto", "edge_auto"]
    found.sort(key=lambda p: (0 if p in preferred else 1, preferred.index(p) if p in preferred else 99, p))
    return found

async def find_profile_for_notebook(notebook_id: str, authuser_hint: int | None = None) -> tuple[str | None, int, str | None]:
    cached_prof, cached_au = get_cached_auth(notebook_id)
    profiles = get_available_profiles()
    if not profiles:
        return None, 0, None

    if cached_prof and cached_prof in profiles:
        profiles = [cached_prof] + [p for p in profiles if p != cached_prof]

    candidate_aus = []
    if cached_au is not None:
        candidate_aus.append(cached_au)
    if authuser_hint is not None and authuser_hint not in candidate_aus:
        candidate_aus.append(authuser_hint)
    for au in [0, 1, 2, 3, 4]:
        if au not in candidate_aus:
            candidate_aus.append(au)

    profiles_dir = Path.home() / ".notebooklm" / "profiles"

    with tempfile.TemporaryDirectory() as td:
        temp_storage = Path(td) / "storage_state.json"

        for p in profiles:
            p_file = profiles_dir / p / "storage_state.json"
            try:
                base_data = json.loads(p_file.read_text(encoding="utf-8"))
            except Exception:
                continue

            for au in candidate_aus:
                base_data["notebooklm"] = {"account": {"authuser": au}, "version": 1}
                temp_storage.write_text(json.dumps(base_data), encoding="utf-8")

                try:
                    async with NotebookLMClient.from_storage(path=str(temp_storage), timeout=4.0) as client:
                        nb = await client.notebooks.get(notebook_id)
                        if nb:
                            title = nb.title or f"Sổ tay #{notebook_id[:8]}"
                            set_cached_auth(notebook_id, p, au, title)
                            return p, au, title
                except Exception:
                    continue

    return None, 0, None

def extract_text_from_file(file_path: Path) -> tuple[str, str]:
    """Returns (text_content, display_title)"""
    ext = file_path.suffix.lower()
    title = file_path.name

    if ext in [".txt", ".md", ".json", ".csv", ".py", ".cs", ".js", ".html", ".css", ".xml", ".log", ".cpp", ".c", ".ts"]:
        for enc in ["utf-8", "utf-8-sig", "cp1252", "latin1"]:
            try:
                return file_path.read_text(encoding=enc), title
            except UnicodeDecodeError:
                continue
        return file_path.read_text(errors="ignore"), title

    elif ext == ".pdf":
        try:
            from pypdf import PdfReader
            reader = PdfReader(str(file_path))
            pages = []
            for i, page in enumerate(reader.pages):
                text = page.extract_text() or ""
                if text.strip():
                    pages.append(f"--- Trang {i+1} ---\n{text.strip()}")
            full_text = "\n\n".join(pages)
            if not full_text.strip():
                full_text = f"Tài liệu PDF: {file_path.name}"
            return full_text, title
        except Exception as ex:
            return f"Lỗi đọc PDF {file_path.name}: {ex}", title

    elif ext == ".docx":
        try:
            import zipfile
            import xml.etree.ElementTree as ET
            with zipfile.ZipFile(str(file_path)) as z:
                xml_data = z.read("word/document.xml")
            tree = ET.fromstring(xml_data)
            paragraphs = []
            for node in tree.iter("{http://schemas.openxmlformats.org/wordprocessingml/2006/main}p"):
                texts = [t.text for t in node.iter("{http://schemas.openxmlformats.org/wordprocessingml/2006/main}t") if t.text]
                if texts:
                    paragraphs.append("".join(texts))
            return "\n\n".join(paragraphs), title
        except Exception as ex:
            return f"Lỗi đọc file Word DOCX {file_path.name}: {ex}", title

    return file_path.read_text(errors="ignore"), title

async def handle_resolve(args):
    nb_id, hint_au = extract_notebook_id_and_authuser(args.notebook)
    profile, authuser, title = await find_profile_for_notebook(nb_id, hint_au)

    if not profile:
        print(json.dumps({
            "success": False,
            "error": f"Không tìm thấy Sổ tay '{nb_id}' trong các tài khoản Google đã đăng nhập.",
            "notebook_id": nb_id
        }, ensure_ascii=False))
        return

    source_count = 0
    try:
        profiles_dir = Path.home() / ".notebooklm" / "profiles"
        p_file = profiles_dir / profile / "storage_state.json"
        base_data = json.loads(p_file.read_text(encoding="utf-8"))
        base_data["notebooklm"] = {"account": {"authuser": authuser}, "version": 1}

        with tempfile.TemporaryDirectory() as td:
            temp_storage = Path(td) / "storage_state.json"
            temp_storage.write_text(json.dumps(base_data), encoding="utf-8")
            async with NotebookLMClient.from_storage(path=str(temp_storage), timeout=4.0) as client:
                sources = await client.sources.list(nb_id)
                source_count = len(sources)
    except Exception:
        pass

    print(json.dumps({
        "success": True,
        "notebook_id": nb_id,
        "title": title or f"Sổ tay #{nb_id[:8]}",
        "profile": profile,
        "authuser": authuser,
        "source_count": source_count
    }, ensure_ascii=False))

async def handle_add_source(args):
    nb_id, hint_au = extract_notebook_id_and_authuser(args.notebook)
    target = args.target.strip()
    source_type = args.type.lower()
    custom_title = args.title or ""

    # 1. Resolve text/file content first
    payload_text = ""
    display_title = custom_title
    if source_type == "file":
        path = Path(target)
        if not path.exists():
            print(json.dumps({"success": False, "error": f"Tệp không tồn tại: {target}"}, ensure_ascii=False))
            return
        payload_text, extracted_title = extract_text_from_file(path)
        if not display_title:
            display_title = extracted_title
    elif source_type == "text":
        payload_text = target
        if not display_title:
            display_title = "Nguồn văn bản"

    # 2. Try find profile or probe across all available
    profile, authuser, nb_title = await find_profile_for_notebook(nb_id, hint_au)

    candidate_attempts = []
    if profile:
        candidate_attempts.append((profile, authuser))

    profiles = get_available_profiles()
    for p in profiles:
        for au in ([hint_au] if hint_au is not None else []) + [0, 1, 2, 3, 4]:
            if (p, au) not in candidate_attempts:
                candidate_attempts.append((p, au))

    profiles_dir = Path.home() / ".notebooklm" / "profiles"
    last_error = "Không thể thêm nguồn vào sổ tay"

    with tempfile.TemporaryDirectory() as td:
        temp_storage = Path(td) / "storage_state.json"

        for p, au in candidate_attempts:
            p_file = profiles_dir / p / "storage_state.json"
            try:
                base_data = json.loads(p_file.read_text(encoding="utf-8"))
            except Exception:
                continue

            base_data["notebooklm"] = {"account": {"authuser": au}, "version": 1}
            temp_storage.write_text(json.dumps(base_data), encoding="utf-8")

            try:
                async with NotebookLMClient.from_storage(path=str(temp_storage), timeout=8.0) as client:
                    if source_type == "url":
                        src = await client.sources.add_url(nb_id, target, title=custom_title if custom_title else None)
                        res_id = getattr(src, "id", "")
                        res_title = getattr(src, "title", target)
                        res_type = "url"
                    elif source_type in ["file", "text"]:
                        src = await client.sources.add_text(nb_id, display_title, payload_text)
                        res_id = getattr(src, "id", "")
                        res_title = getattr(src, "title", display_title)
                        res_type = source_type
                    else:
                        print(json.dumps({"success": False, "error": f"Loại nguồn không hỗ trợ: {source_type}"}, ensure_ascii=False))
                        return

                    set_cached_auth(nb_id, p, au, nb_title or "")

                    print(json.dumps({
                        "success": True,
                        "notebook_id": nb_id,
                        "notebook_title": nb_title or "",
                        "source_id": res_id,
                        "title": res_title,
                        "type": res_type,
                        "profile": p,
                        "authuser": au
                    }, ensure_ascii=False))
                    return

            except Exception as ex:
                last_error = str(ex)
                continue

    print(json.dumps({
        "success": False,
        "error": f"Không thể nạp nguồn vào Sổ tay '{nb_id}': {last_error}",
        "notebook_id": nb_id
    }, ensure_ascii=False))

def main():
    parser = argparse.ArgumentParser(description="NotebookLM Direct API Bridge")
    parser.add_argument("--action", required=True, choices=["resolve", "add_source"])
    parser.add_argument("--notebook", required=True, help="Notebook URL or UUID")
    parser.add_argument("--type", choices=["url", "file", "text"], default="url")
    parser.add_argument("--target", help="URL, file path, or text content")
    parser.add_argument("--title", help="Optional title")

    args = parser.parse_args()

    if args.action == "resolve":
        asyncio.run(handle_resolve(args))
    elif args.action == "add_source":
        if not args.target:
            print(json.dumps({"success": False, "error": "--target is required for add_source"}))
            sys.exit(1)
        asyncio.run(handle_add_source(args))

if __name__ == "__main__":
    main()
