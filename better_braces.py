# better_braces.py
"""
    Safely formats C# files across the project:
    1. Replaces non-breaking spaces (\u00a0) with regular spaces.
    2. Attaches '{' directly to declaration lines without extra spaces:
        - 'namespace UwpPdfViewer\n{' -> 'namespace UwpPdfViewer{'
        - 'public sealed partial class MainPage : Page\n{' -> 'public sealed partial class MainPage : Page{'
        - Safely places '{' before trailing comments (e.g. 'if (cond){ // comment').
    3. Eliminates unnecessary enters (blank lines):
        - Immediately after '}' before statements
        - Immediately after '{' or before '}'
        - Between statements ending in ';' and subsequent code or comments ('//')
        - Between consecutive comments ('//')
        - Around attributes ('[...]') and using directives
        - Collapses multiple consecutive blank lines anywhere down to at most one
    4. Collapses single-line and empty blocks to one line:
        - 'catch{\n    statement;\n}' -> 'catch{statement;}'
        - 'catch{\n}' -> 'catch{}'
    5. Safety invariant: verifies exact count of '{' and '}' before and after.
    6. Optional: --remove-bak flag to delete backup (*.bak) files after processing.
"""
import argparse, ast, os, re, shutil, sys
from pathlib import Path
IGNORED_DIRS: set[str] = {
    "bin", "obj", ".git", ".vs", ".vscode", ".idea", "node_modules",
    "packages", "Assets", "Releases", "dist", "build", "out",
    "TestResults", "artifacts","ephe", "opus-1.5.2"
}
IGNORED_EXTENSIONS: set[str] = {
    # Certificates, keys, signatures
    ".cer", ".pfx", ".p12", ".snk", ".key", ".pem", ".crt",
    # Binaries, libraries, executables, symbols
    ".dll", ".exe", ".pdb", ".so", ".dylib", ".lib", ".a", ".bin", ".winmd",
    # Manifests and application definitions
    ".manifest",
    # Media, images, fonts
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".bmp", ".webp", ".mp4", ".mp3", ".wav",
    ".ttf", ".otf", ".woff", ".woff2", "ico"
    # Archives and packages
    ".zip", ".7z", ".tar", ".gz", ".nupkg", ".snupkg",
    # Documents
    ".pdf", ".doc", ".docx",".txt", ".json"
}
IGNORED_FILENAMES: set[str] = {
    ".gitattributes", ".gitignore", ".gitmodules", ".editorconfig",
}

def is_ignored_file(path: Path) -> bool:
    """Determines if a file is an excluded binary, certificate, manifest, or configuration file."""
    return path.name.lower() in IGNORED_FILENAMES or path.suffix.lower() in IGNORED_EXTENSIONS
BRACE_EXTENSIONS: set[str] = {
    ".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".h++",
    ".inl", ".ipp", ".tpp", ".m", ".mm", ".cs", ".csx", ".java", ".kt", ".kts",
    ".scala", ".sc", ".groovy", ".gvy", ".gradle", ".js", ".jsx", ".mjs", ".cjs",
    ".ts", ".tsx", ".mts", ".cts", ".rs", ".go", ".swift", ".dart", ".zig",
    ".d", ".di", ".odin", ".v", ".sv", ".svh", ".vala", ".vapi", ".hx", ".as",
    ".php", ".sol", ".glsl", ".hlsl", ".vert", ".frag", ".geom", ".comp",
    ".wgsl", ".shader", ".proto", ".thrift", ".jsonc", ".json5",
    ".css", ".scss", ".sass", ".less", ".qss", ".pcss", 
}
PYTHON_EXTENSIONS: set[str] = {".py", ".pyw", ".pyi", ".pyx", ".pxd"}
MARKUP_EXTENSIONS: set[str] = {
    ".xml", ".xsd", ".xsl", ".xslt", ".wsdl", ".svg", ".kml", ".gpx", ".rss",
    ".atom", ".plist", ".xaml", ".axml", ".csproj", ".fsproj", ".vbproj",
    ".vcxproj", ".props", ".targets", ".config", ".resx", ".pom", ".html",
    ".htm", ".xhtml", ".vue", ".svelte", ".astro",
}
COMMENT_TEMPLATES: dict[str, str] = {
    # -------------------------------------------------------------
    # Double-slash comments (// ...)
    # -------------------------------------------------------------
    # C / C++ family
    ".c": "// {path}",
    ".cc": "// {path}",
    ".cpp": "// {path}",
    ".cxx": "// {path}",
    ".c++": "// {path}",
    ".h": "// {path}",
    ".hh": "// {path}",
    ".hpp": "// {path}",
    ".hxx": "// {path}",
    ".h++": "// {path}",
    ".inl": "// {path}",
    ".ipp": "// {path}",
    ".tpp": "// {path}",
    # Objective-C / Objective-C++
    ".m": "// {path}",
    ".mm": "// {path}",
    # C# / .NET
    ".cs": "// {path}",
    ".csx": "// {path}",
    # Java & JVM languages
    ".java": "// {path}",
    ".kt": "// {path}",
    ".kts": "// {path}",
    ".scala": "// {path}",
    ".sc": "// {path}",
    ".groovy": "// {path}",
    ".gvy": "// {path}",
    ".gradle": "// {path}",
    # JavaScript / TypeScript ecosystem
    ".js": "// {path}",
    ".jsx": "// {path}",
    ".mjs": "// {path}",
    ".cjs": "// {path}",
    ".ts": "// {path}",
    ".tsx": "// {path}",
    ".mts": "// {path}",
    ".cts": "// {path}",
    # Systems & Compiled languages
    ".rs": "// {path}",
    ".go": "// {path}",
    ".swift": "// {path}",
    ".dart": "// {path}",
    ".zig": "// {path}",
    ".d": "// {path}",
    ".di": "// {path}",
    ".odin": "// {path}",
    ".v": "// {path}",       # Verilog / Vlang
    ".sv": "// {path}",      # SystemVerilog
    ".svh": "// {path}",
    ".vala": "// {path}",
    ".vapi": "// {path}",
    ".hx": "// {path}",      # Haxe
    ".as": "// {path}",      # ActionScript
    # F# / OCaml-compatible single line
    ".fs": "// {path}",
    ".fsi": "// {path}",
    ".fsx": "// {path}",
    # Web scripting / contracts
    ".php": "// {path}",
    ".sol": "// {path}",     # Solidity
    # Shaders
    ".glsl": "// {path}",
    ".hlsl": "// {path}",
    ".vert": "// {path}",
    ".frag": "// {path}",
    ".geom": "// {path}",
    ".comp": "// {path}",
    ".wgsl": "// {path}",
    ".shader": "// {path}",
    # Schemas & configs supporting //
    ".proto": "// {path}",   # Protocol Buffers
    ".thrift": "// {path}",
    ".graphql": "// {path}",
    ".gql": "// {path}",
    ".jsonc": "// {path}",
    ".json5": "// {path}",
    # -------------------------------------------------------------
    # Hash comments (# ...)
    # -------------------------------------------------------------
    # Python
    ".py": "# {path}",
    ".pyw": "# {path}",
    ".pyi": "# {path}",
    ".pyx": "# {path}",
    ".pxd": "# {path}",
    # Shell / Terminal
    ".sh": "# {path}",
    ".bash": "# {path}",
    ".zsh": "# {path}",
    ".ksh": "# {path}",
    ".csh": "# {path}",
    ".tcsh": "# {path}",
    ".fish": "# {path}",
    # PowerShell
    ".ps1": "# {path}",
    ".psm1": "# {path}",
    ".psd1": "# {path}",
    # Ruby
    ".rb": "# {path}",
    ".rbw": "# {path}",
    ".rake": "# {path}",
    ".gemspec": "# {path}",
    # Perl / Raku
    ".pl": "# {path}",
    ".pm": "# {path}",
    ".t": "# {path}",
    ".raku": "# {path}",
    ".rakumod": "# {path}",
    # Other scripting & dynamic languages
    ".r": "# {path}",
    ".jl": "# {path}",       # Julia
    ".ex": "# {path}",       # Elixir
    ".exs": "# {path}",
    ".cr": "# {path}",       # Crystal
    ".nim": "# {path}",
    ".nims": "# {path}",
    ".nimble": "# {path}",
    ".tcl": "# {path}",
    ".tk": "# {path}",
    ".awk": "# {path}",
    ".sed": "# {path}",
    ".coffee": "# {path}",
    # Config & Data formats
    ".yaml": "# {path}",
    ".yml": "# {path}",
    ".toml": "# {path}",
    ".ini": "# {path}",
    ".cfg": "# {path}",
    ".conf": "# {path}",
    ".env": "# {path}",
    ".properties": "# {path}",
    ".editorconfig": "# {path}",
    ".gitignore": "# {path}",
    ".gitattributes": "# {path}",
    ".gitmodules": "# {path}",
    # Build & Infrastructure
    ".dockerfile": "# {path}",
    ".containerfile": "# {path}",
    ".mk": "# {path}",
    ".mak": "# {path}",
    ".cmake": "# {path}",
    ".tf": "# {path}",       # Terraform / HCL
    ".tfvars": "# {path}",
    ".hcl": "# {path}",
    ".nomad": "# {path}",
    # -------------------------------------------------------------
    # Dash-dash comments (-- ...)
    # -------------------------------------------------------------
    ".sql": "-- {path}",
    ".lua": "-- {path}",
    ".hs": "-- {path}",       # Haskell
    ".lhs": "-- {path}",
    ".elm": "-- {path}",
    ".purs": "-- {path}",     # PureScript
    ".adb": "-- {path}",      # Ada
    ".ads": "-- {path}",
    ".vhd": "-- {path}",      # VHDL
    ".vhdl": "-- {path}",
    ".applescript": "-- {path}",
    ".e": "-- {path}",        # Eiffel
    # -------------------------------------------------------------
    # Semicolon comments (; ...)
    # -------------------------------------------------------------
    # Assembly
    ".asm": "; {path}",
    ".s": "; {path}",
    ".inc": "; {path}",
    ".a51": "; {path}",
    # Lisp / Scheme family
    ".lisp": "; {path}",
    ".lsp": "; {path}",
    ".cl": "; {path}",
    ".clj": "; {path}",      # Clojure
    ".cljs": "; {path}",
    ".cljc": "; {path}",
    ".edn": "; {path}",
    ".scm": "; {path}",      # Scheme
    ".ss": "; {path}",
    ".rkt": "; {path}",      # Racket
    ".el": "; {path}",       # Emacs Lisp
    # Windows scripting & configs
    ".ahk": "; {path}",      # AutoHotkey
    ".au3": "; {path}",      # AutoIt
    ".inf": "; {path}",
    # -------------------------------------------------------------
    # Percent comments (% ...)
    # -------------------------------------------------------------
    ".tex": "% {path}",      # LaTeX / TeX
    ".sty": "% {path}",
    ".cls": "% {path}",
    ".dtx": "% {path}",
    ".ins": "% {path}",
    ".erl": "% {path}",      # Erlang
    ".hrl": "% {path}",
    ".pro": "% {path}",      # Prolog
    ".prolog": "% {path}",
    ".ps": "% {path}",       # PostScript
    ".eps": "% {path}",
    # -------------------------------------------------------------
    # Exclamation mark comments (! ...)
    # -------------------------------------------------------------
    ".f": "! {path}",        # Fortran
    ".for": "! {path}",
    ".f90": "! {path}",
    ".f95": "! {path}",
    ".f03": "! {path}",
    ".f08": "! {path}",
    ".xresources": "! {path}",
    ".xdefaults": "! {path}",
    # -------------------------------------------------------------
    # Apostrophe / Single-quote comments (' ...)
    # -------------------------------------------------------------
    ".vb": "' {path}",       # Visual Basic / VB.NET
    ".vbs": "' {path}",
    ".bas": "' {path}",
    ".frm": "' {path}",
    # -------------------------------------------------------------
    # Quotation mark comments (" ...)
    # -------------------------------------------------------------
    ".vim": "\" {path}",     # Vimscript
    ".vimrc": "\" {path}",
    # -------------------------------------------------------------
    # Block comments (/* ... */)
    # -------------------------------------------------------------
    ".css": "/* {path} */",
    ".scss": "/* {path} */",
    ".sass": "/* {path} */",
    ".less": "/* {path} */",
    ".qss": "/* {path} */",
    ".pcss": "/* {path} */",  # PostCSS
    ".styl": "/* {path} */",  # Stylus
    # -------------------------------------------------------------
    # Parenthesis-asterisk comments ((* ... *))
    # -------------------------------------------------------------
    ".ml": "(* {path} *)",   # OCaml
    ".mli": "(* {path} *)",
    ".sml": "(* {path} *)",  # Standard ML
    ".sig": "(* {path} *)",
    ".pas": "(* {path} *)",  # Pascal / Delphi
    ".pp": "(* {path} *)",
    ".dpr": "(* {path} *)",
    # -------------------------------------------------------------
    # Markup comments (<!-- ... -->)
    # -------------------------------------------------------------
    # Web & UI Components
    ".html": "<!-- {path} -->",
    ".htm": "<!-- {path} -->",
    ".xhtml": "<!-- {path} -->",
    ".vue": "<!-- {path} -->",
    ".svelte": "<!-- {path} -->",
    ".astro": "<!-- {path} -->",
    ".markdown": "<!-- {path} -->",
    ".md": "<!-- {path} -->",
    ".mdown": "<!-- {path} -->",
    ".mdx": "<!-- {path} -->",
    ".mjml": "<!-- {path} -->",
    # XML formats
    ".xml": "<!-- {path} -->",
    ".xsd": "<!-- {path} -->",
    ".xsl": "<!-- {path} -->",
    ".xslt": "<!-- {path} -->",
    ".wsdl": "<!-- {path} -->",
    ".svg": "<!-- {path} -->",
    ".kml": "<!-- {path} -->",
    ".gpx": "<!-- {path} -->",
    ".rss": "<!-- {path} -->",
    ".atom": "<!-- {path} -->",
    ".plist": "<!-- {path} -->",
    # XAML & .NET project files
    ".xaml": "<!-- {path} -->",
    ".axml": "<!-- {path} -->",
    ".csproj": "<!-- {path} -->",
    ".fsproj": "<!-- {path} -->",
    ".vbproj": "<!-- {path} -->",
    ".vcxproj": "<!-- {path} -->",
    ".appxmanifest": "<!-- {path} -->",
    ".props": "<!-- {path} -->",
    ".targets": "<!-- {path} -->",
    ".config": "<!-- {path} -->",
    ".resx": "<!-- {path} -->",
    ".pom": "<!-- {path} -->",
    # -------------------------------------------------------------
    # Template engines
    # -------------------------------------------------------------
    ".razor": "@* {path} *@",   # Blazor / Razor
    ".cshtml": "@* {path} *@",
    ".vbhtml": "@* {path} *@",
    ".jinja": "{# {path} #}",   # Jinja / Nunjucks / Twig
    ".jinja2": "{# {path} #}",
    ".j2": "{# {path} #}",
    ".twig": "{# {path} #}",
}

def apply_header_comment(content: str, rel_path: str, ext: str) -> tuple[str, bool]:
    """
    Inserts a relative path comment at the top of the file.
    Preserves shebangs (#!...) and XML declarations (<?xml...) on line 1.
    """
    template = COMMENT_TEMPLATES.get(ext.lower())
    if not template: return content, False
    comment_line = template.format(path=rel_path)
    has_crlf = "\r\n" in content
    newline = "\r\n" if has_crlf else "\n"
    lines = content.split(newline)
    insert_idx = 0
    if lines:
        first_line = lines[0].strip()
        if first_line.startswith("#!") or first_line.startswith("<?xml"):
            insert_idx = 1
    if insert_idx < len(lines) and lines[insert_idx].strip() == comment_line.strip(): return content, False
    lines.insert(insert_idx, comment_line)
    return newline.join(lines), True

def split_code_and_comment(line: str) -> tuple[str, str]:
    """Splits a line into (code, comment), respecting string literals."""
    in_quote = False
    is_verbatim = False
    quote_char = ""
    i = 0
    n = len(line)
    while i < n:
        ch = line[i]
        if not in_quote:
            if ch == "@" and i + 1 < n and line[i + 1] == '"':
                in_quote = True
                is_verbatim = True
                quote_char = '"'
                i += 2
                continue
            elif ch in ('"', "'", "`"):
                in_quote = True
                is_verbatim = False
                quote_char = ch
                i += 1
                continue
            elif i + 1 < n and line[i : i + 2] == "//": return line[:i], line[i:]
        else:
            if is_verbatim:
                if ch == '"':
                    if i + 1 < n and line[i + 1] == '"':
                        i += 2
                        continue
                    else:
                        in_quote = False
            else:
                if ch == "\\" and i + 1 < n:
                    i += 2
                    continue
                elif ch == quote_char:
                    in_quote = False
        i += 1
    return line, ""

def mark_protected_lines(raw_lines: list[str]) -> list[bool]:
    """
    Marks lines that fall inside multiline comments (/* ... */),
    verbatim string literals (@"..."), raw string literals (\"\"\"...\"\"\"),
    or template literals (`...`).
    Protected lines are never modified or dropped.
    """
    is_protected = [False] * len(raw_lines)
    in_block_comment = False
    in_verbatim_string = False
    in_raw_string = False
    in_backtick = False
    for idx, line in enumerate(raw_lines):
        if in_block_comment or in_verbatim_string or in_raw_string or in_backtick:
            is_protected[idx] = True
        i = 0
        n = len(line)
        while i < n:
            if in_block_comment:
                if i + 1 < n and line[i : i + 2] == "*/":
                    in_block_comment = False
                    i += 2
                    continue
                i += 1
            elif in_verbatim_string:
                if line[i] == '"':
                    if i + 1 < n and line[i + 1] == '"':
                        i += 2
                        continue
                    else:
                        in_verbatim_string = False
                        i += 1
                        continue
                i += 1
            elif in_raw_string:
                if i + 2 < n and line[i : i + 3] == '"""':
                    in_raw_string = False
                    i += 3
                    continue
                i += 1
            elif in_backtick:
                if line[i] == "\\" and i + 1 < n:
                    i += 2
                    continue
                if line[i] == "`":
                    in_backtick = False
                    i += 1
                    continue
                i += 1
            else:
                if i + 1 < n and line[i : i + 2] == "//":
                    break
                if i + 1 < n and line[i : i + 2] == "/*":
                    in_block_comment = True
                    is_protected[idx] = True
                    i += 2
                    continue
                if i + 2 < n and line[i : i + 3] == '"""':
                    in_raw_string = True
                    is_protected[idx] = True
                    i += 3
                    continue
                if line[i] == "`":
                    in_backtick = True
                    is_protected[idx] = True
                    i += 1
                    continue
                if (line[i] == "@" and i + 1 < n and line[i + 1] == '"') or (
                    i + 2 < n and line[i : i + 2] in ("$@", "@$") and line[i + 2] == '"'
                ):
                    in_verbatim_string = True
                    is_protected[idx] = True
                    i += 3 if line[i] in ("$", "@") and line[i + 1] in ("$", "@") else 2
                    continue
                if line[i] == '"':
                    i += 1
                    while i < n:
                        if line[i] == "\\" and i + 1 < n:
                            i += 2
                            continue
                        elif line[i] == '"':
                            i += 1
                            break
                        i += 1
                    continue
                if line[i] == "'":
                    i += 1
                    while i < n:
                        if line[i] == "\\" and i + 1 < n:
                            i += 2
                            continue
                        elif line[i] == "'":
                            i += 1
                            break
                        i += 1
                    continue
                i += 1
        if in_block_comment or in_verbatim_string or in_raw_string or in_backtick:
            is_protected[idx] = True
    return is_protected

def is_brace_function_ahead(lines: list[str], start_idx: int) -> bool:
    """Checks if the next non-blank, non-comment construct is a function, method, or class header."""
    idx = start_idx
    n = len(lines)
    while idx < n:
        s = lines[idx].strip()
        if not s:
            idx += 1
            continue
        if s.startswith("//") or s.startswith("/*") or s.endswith("*/"):
            idx += 1
            continue
        if s.startswith(("[", "@")):
            idx += 1
            continue
        code, _ = split_code_and_comment(s)
        code_trim = code.strip()
        if not code_trim:
            idx += 1
            continue
        if re.search(r"\b(class|struct|interface|enum|record|trait|impl|namespace)\b", code_trim): return True
        if re.search(r"\b(function|func|fn|fun|def)\b", code_trim): return True
        if re.match(
            r"^(public|private|protected|internal|static|virtual|override|sealed|abstract|async|inline|extern|unsafe|default|final|native|synchronized)\b",
            code_trim,
        ):
            return True
        if "(" in code_trim and (code_trim.endswith("{") or "=>" in code_trim):
            first_word = re.split(r"\W+", code_trim, maxsplit=1)[0]
            control_words = {
                "if", "else", "for", "foreach", "while", "do", "switch", "case",
                "catch", "finally", "try", "using", "lock", "fixed", "return", "throw",
            }
            if first_word not in control_words and "=" not in code_trim.split("(")[0]: return True
        return False
    return False
CONTINUATION_END_OPS = (
    "||", "&&", "==", "!=", "<=", ">=", "??", "??=", "=>",
    "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=",
    "<<", ">>", "+", "-", "*", "/", "%", "|", "&", "^", "=", "?", ",", ".", "?.", "(", "[",
)
CONTINUATION_START_OPS = (
    "||", "&&", "==", "!=", "<=", ">=", "??", "??=", "=>",
    "|", "^", "?", "+", "-", "*", "/", "%", ".", "?.", ",", ")", "]",
)

def get_delimiter_deltas(code: str) -> tuple[int, int]:
    """Returns (delta_parens, delta_brackets) in code, ignoring strings and characters."""
    parens, brackets = 0, 0
    in_quote, is_verbatim, quote_char = False, False, ""
    i, n = 0, len(code)
    while i < n:
        ch = code[i]
        if not in_quote:
            if ch == "@" and i + 1 < n and code[i + 1] == '"':
                in_quote, is_verbatim, quote_char, i = True, True, '"', i + 2
                continue
            elif ch in ('"', "'", "`"):
                in_quote, is_verbatim, quote_char, i = True, False, ch, i + 1
                continue
            elif i + 1 < n and code[i : i + 2] == "//": break
            elif ch == "(": parens += 1
            elif ch == ")": parens -= 1
            elif ch == "[": brackets += 1
            elif ch == "]": brackets -= 1
        else:
            if is_verbatim:
                if ch == '"':
                    if i + 1 < n and code[i + 1] == '"':
                        i += 2
                        continue
                    else: in_quote = False
            else:
                if ch == "\\" and i + 1 < n:
                    i += 2
                    continue
                elif ch == quote_char: in_quote = False
        i += 1
    return parens, brackets

def is_continuation_end(code: str) -> bool:
    c = code.rstrip()
    if not c or c.endswith(("{", "}", ";")) or c.startswith("#"): return False
    if c.endswith(CONTINUATION_END_OPS): return True
    if c.endswith(":"):
        if c.startswith("case ") or c.startswith("default:") or c == "default:": return False
        if c in ("public:", "private:", "protected:", "internal:"): return False
        if re.match(r"^[A-Za-z_][A-Za-z0-9_]*\s*:$", c): return False
        return True
    if c in ("return", "throw", "yield return", "await") or any(c.endswith(" " + kw) for kw in ("return", "throw", "yield return", "await")): return True
    return False

def is_continuation_start(code: str) -> bool:
    c = code.strip()
    if not c or c.startswith(("{", "}", "#")) or c.startswith("//"): return False
    if c.startswith(CONTINUATION_START_OPS): return True
    if c.startswith(":") and not c.startswith("::"): return True
    return False

def can_join_lines(codeA: str, commA: str, protA: bool,
                   codeB: str, commB: str, protB: bool,
                   paren_depth: int, bracket_depth: int) -> bool:
    if protA or protB or commA.strip() != "": return False
    codeA_trim = codeA.rstrip()
    codeB_trim = codeB.strip()
    if not codeA_trim or not codeB_trim: return False
    if codeA_trim.startswith("#") or codeB_trim.startswith("#"): return False
    if codeA_trim.endswith(("{", "}")) or codeB_trim.startswith(("{", "}")): return False
    if paren_depth == 0 and bracket_depth == 0:
        if codeA_trim.startswith("[") and codeA_trim.endswith("]"): return False
        if codeA_trim.endswith(";"): return False
    is_cont = (paren_depth > 0 or bracket_depth > 0 or is_continuation_end(codeA_trim) or is_continuation_start(codeB_trim))
    if not is_cont: return False
    if commB.strip() != "":
        d_p, d_b = get_delimiter_deltas(codeB)
        return (paren_depth + d_p <= 0 and bracket_depth + d_b <= 0) and codeB_trim.endswith(";")
    return True

def join_code_lines(codeA: str, codeB: str) -> str:
    cA = codeA.rstrip()
    cB = codeB.strip()
    if cA.endswith(("(", "[", ".", "?.")) or cB.startswith((")", "]", ",", ".", "?.")): return cA + cB
    return cA + " " + cB

def condense_multiline_items(lines: list[str], is_protected: list[bool]) -> tuple[list[str], list[bool]]:
    result_lines: list[str] = []
    result_protected: list[bool] = []
    i, n = 0, len(lines)
    while i < n:
        cur_line, cur_prot = lines[i], is_protected[i]
        if cur_prot:
            result_lines.append(cur_line)
            result_protected.append(True)
            i += 1
            continue
        codeA, commA = split_code_and_comment(cur_line)
        dp, db = get_delimiter_deltas(codeA)
        paren_depth, bracket_depth = max(0, dp), max(0, db)
        j = i + 1
        while j < n:
            next_line, next_prot = lines[j], is_protected[j]
            codeB, commB = split_code_and_comment(next_line)
            if can_join_lines(codeA, commA, cur_prot, codeB, commB, next_prot, paren_depth, bracket_depth):
                codeA = join_code_lines(codeA, codeB)
                commA = commB
                d_p, d_b = get_delimiter_deltas(codeB)
                paren_depth, bracket_depth = max(0, paren_depth + d_p), max(0, bracket_depth + d_b)
                cur_line = codeA + commA
                j += 1
            else: break
        result_lines.append(cur_line)
        result_protected.append(False)
        i = j
    return result_lines, result_protected

def format_brace_source(content: str, filename: str = "", ext: str = ".cs") -> tuple[str, int, int]:
    has_crlf = "\r\n" in content
    normalized = content.replace("\r\n", "\n").replace("\u00a0", " ")
    raw_lines = normalized.split("\n")
    orig_open_braces = content.count("{")
    orig_close_braces = content.count("}")
    is_protected = mark_protected_lines(raw_lines)
    # -------------------------------------------------------------------------
    # Pass 1: Attach standalone opening brace '{' directly to previous line
    # -------------------------------------------------------------------------
    merged_lines = []
    merged_protected = []
    braces_attached = 0
    i = 0
    total_lines = len(raw_lines)
    while i < total_lines:
        line = raw_lines[i]
        stripped = line.strip()
        if not is_protected[i] and stripped == "{":
            target_idx = len(merged_lines) - 1
            while target_idx >= 0 and merged_lines[target_idx].strip() == "":
                target_idx -= 1
            if target_idx >= 0 and not merged_protected[target_idx]:
                prev_line = merged_lines[target_idx]
                code_part, comment_part = split_code_and_comment(prev_line)
                code_trimmed = code_part.rstrip()
                if (
                    code_trimmed != ""
                    and not code_trimmed.startswith("#")
                    and not code_trimmed.endswith("*/")
                    and not code_trimmed.endswith("{")
                ):
                    if comment_part:
                        space = " " if not comment_part.startswith(" ") else ""
                        merged_lines[target_idx] = f"{code_trimmed}{{{space}{comment_part}"
                    else:
                        merged_lines[target_idx] = f"{code_trimmed}{{"
                    del merged_lines[target_idx + 1 :]
                    del merged_protected[target_idx + 1 :]
                    braces_attached += 1
                    i += 1
                    continue
        if not is_protected[i]:
            code_p, comment_p = split_code_and_comment(line)
            if re.search(r"\b(namespace|class|struct|interface|enum|record|function|func|fn|impl|trait)\b", code_p):
                if code_p.rstrip().endswith(" {"):
                    code_p = code_p.rstrip()[:-2] + "{"
                    line = code_p + comment_p
        merged_lines.append(line)
        merged_protected.append(is_protected[i])
        i += 1
    # -------------------------------------------------------------------------
    # Pass 2: Remove unnecessary blank lines (enters)
    # -------------------------------------------------------------------------
    filtered_lines = []
    filtered_protected = []
    blank_lines_removed = 0
    m_len = len(merged_lines)
    for j in range(m_len):
        cur = merged_lines[j]
        cur_stripped = cur.strip()
        if cur_stripped == "":
            if merged_protected[j]:
                filtered_lines.append(cur)
                filtered_protected.append(True)
                continue
            prev_raw = ""
            prev_code = ""
            for k in range(len(filtered_lines) - 1, -1, -1):
                s = filtered_lines[k].strip()
                if s != "":
                    prev_raw = s
                    code, _ = split_code_and_comment(s)
                    prev_code = code.strip()
                    break
            next_raw = ""
            next_code = ""
            for k in range(j + 1, m_len):
                s = merged_lines[k].strip()
                if s != "":
                    next_raw = s
                    code, _ = split_code_and_comment(s)
                    next_code = code.strip()
                    break
            drop = False
            # 1. Drop leading blank lines at the top of file
            if len(filtered_lines) == 0:
                drop = True
            # 2. Drop consecutive blank lines (collapse down to at most one enter)
            elif len(filtered_lines) > 0 and filtered_lines[-1].strip() == "":
                drop = True
            # 3. Preserve at most one enter before a function, method, or class
            elif is_brace_function_ahead(merged_lines, j + 1):
                if prev_raw.startswith(("[", "@")) or (prev_raw.startswith("//") and not prev_code):
                    drop = True
                else:
                    drop = False
            # 4. Drop blank line immediately after '{' or before '}'
            elif prev_code.endswith("{") or next_code.startswith("}") or next_raw.startswith("}"):
                drop = True
            # 5. Drop blank line immediately after '}' when followed by statements
            elif prev_code.endswith("}") or prev_raw.endswith("}"):
                if (next_code != "" or next_raw.startswith("//")) and not next_code.startswith("#"):
                    drop = True
            # 6. Drop blank lines around preprocessor regions
            elif prev_raw.startswith("#region") or next_raw.startswith("#endregion"):
                drop = True
            # 7. Drop blank lines around attributes, annotations, and decorators
            elif prev_raw.startswith(("[", "@")) or next_raw.startswith(("[", "@")):
                drop = True
            # 8. Drop blank lines between using / import / include directives
            elif (
                prev_raw.startswith(("using ", "import ", "from ", "#include ", "use ", "package "))
                and next_raw.startswith(("using ", "import ", "from ", "#include ", "use ", "package "))
            ):
                drop = True
            # 9. Drop blank lines around single-line comments ('//')
            elif next_raw.startswith("//") and (prev_code.endswith(";") or prev_code.endswith("{") or prev_raw.startswith("//")):
                drop = True
            elif prev_raw.startswith("//") and (next_raw.startswith("//") or next_code != ""):
                drop = True
            # 10. Drop blank lines after statements ending in ';'
            elif prev_code.endswith(";"):
                if (next_code != "" or next_raw.startswith("//")) and not next_code.startswith("#"):
                    drop = True
            if drop:
                blank_lines_removed += 1
                continue
        filtered_lines.append(cur)
        filtered_protected.append(merged_protected[j])
    # -------------------------------------------------------------------------
    # Pass 3: Condense multi-line statements and expressions into a single line
    # -------------------------------------------------------------------------
    condensed_lines, condensed_protected = condense_multiline_items(filtered_lines, filtered_protected)
    # -------------------------------------------------------------------------
    # Pass 4: Collapse single-statement and empty blocks to one line
    # -------------------------------------------------------------------------
    collapsed_lines = []
    k = 0
    f_len = len(condensed_lines)
    while k < f_len:
        lineA = condensed_lines[k]
        protA = condensed_protected[k]
        codeA, commA = split_code_and_comment(lineA)
        codeA_trim = codeA.rstrip()
        is_block_head = (
            not protA
            and commA == ""
            and codeA_trim.endswith("{")
            and not codeA_trim.startswith("#")
            and not re.search(r"\b(namespace|class|struct|interface|enum|record)\b", codeA_trim)
        )
        if is_block_head:
            # 1. Empty block: lineA\n} -> lineA}
            if k + 1 < f_len and not condensed_protected[k + 1]:
                lineB = condensed_lines[k + 1]
                codeB, commB = split_code_and_comment(lineB)
                strB = codeB.strip()
                if commB == "" and strB in ("}", "};"):
                    collapsed_lines.append(codeA_trim + strB)
                    k += 2
                    continue
            # 2. Single-line statement block: lineA\nlineB\n} -> lineA + lineB + }
            if k + 2 < f_len and not condensed_protected[k + 1] and not condensed_protected[k + 2]:
                lineB = condensed_lines[k + 1]
                lineC = condensed_lines[k + 2]
                codeB, commB = split_code_and_comment(lineB)
                codeC, commC = split_code_and_comment(lineC)
                strB = codeB.strip()
                strC = codeC.strip()
                if (
                    commB == ""
                    and commC == ""
                    and strB != ""
                    and "{" not in strB
                    and "}" not in strB
                    and not strB.startswith("#")
                    and strC in ("}", "};")
                ):
                    collapsed_lines.append(codeA_trim + strB + strC)
                    k += 3
                    continue
        collapsed_lines.append(lineA)
        k += 1
    while collapsed_lines and collapsed_lines[-1].strip() == "":
        collapsed_lines.pop()
        blank_lines_removed += 1
    newline = "\r\n" if has_crlf else "\n"
    result = newline.join(collapsed_lines) + newline
    new_open_braces = result.count("{")
    new_close_braces = result.count("}")
    if new_open_braces != orig_open_braces or new_close_braces != orig_close_braces:
        raise ValueError(
            f"Brace count mismatch in {filename}! "
            f"Original: [open={orig_open_braces}, close={orig_close_braces}] vs "
            f"Result: [open={new_open_braces}, close={new_close_braces}]. "
            "Reverting to prevent syntax corruption."
        )
    return result, braces_attached, blank_lines_removed

def format_csharp_source(content: str, filename: str = "") -> tuple[str, int, int]:
    """Compatibility alias for C# files."""
    return format_brace_source(content, filename, ".cs")

def is_python_function_ahead(lines: list[str], start_idx: int) -> bool:
    """Checks if the next non-blank, non-comment line is a function or class definition."""
    idx = start_idx
    n = len(lines)
    while idx < n:
        s = lines[idx].strip()
        if not s:
            idx += 1
            continue
        if s.startswith("#"):
            idx += 1
            continue
        if s.startswith("@"):
            idx += 1
            continue
        return s.startswith(("def ", "async def ", "class "))
    return False

def format_python_source(content: str, filename: str = "") -> tuple[str, int]:
    """
    Condenses Python files:
    1. Protects multiline docstrings and strings (''' and \"\"\").
    2. Preserves at most one enter before function, class, and method definitions.
    3. Drops unnecessary blank lines between statements inside functions.
    4. Collapses single-statement function/class/block definitions to one line where valid.
    5. Validates AST to guarantee syntax integrity before applying.
    """
    has_crlf = "\r\n" in content
    normalized = content.replace("\r\n", "\n").replace("\u00a0", " ")
    raw_lines = normalized.split("\n")
    is_protected = [False] * len(raw_lines)
    in_triple_double = False
    in_triple_single = False
    for idx, line in enumerate(raw_lines):
        if in_triple_double or in_triple_single:
            is_protected[idx] = True
        i = 0
        n = len(line)
        while i < n:
            if in_triple_double:
                if i + 2 < n and line[i : i + 3] == '"""':
                    in_triple_double = False
                    i += 3
                    continue
                i += 1
            elif in_triple_single:
                if i + 2 < n and line[i : i + 3] == "'''":
                    in_triple_single = False
                    i += 3
                    continue
                i += 1
            else:
                if line[i] == "#":
                    break
                if i + 2 < n and line[i : i + 3] == '"""':
                    in_triple_double = True
                    is_protected[idx] = True
                    i += 3
                    continue
                if i + 2 < n and line[i : i + 3] == "'''":
                    in_triple_single = True
                    is_protected[idx] = True
                    i += 3
                    continue
                if line[i] in ('"', "'"):
                    q = line[i]
                    i += 1
                    while i < n:
                        if line[i] == "\\" and i + 1 < n:
                            i += 2
                            continue
                        if line[i] == q:
                            i += 1
                            break
                        i += 1
                    continue
                i += 1
        if in_triple_double or in_triple_single:
            is_protected[idx] = True
    filtered_lines: list[str] = []
    filtered_protected: list[bool] = []
    blanks_removed = 0
    for j in range(len(raw_lines)):
        cur = raw_lines[j]
        cur_stripped = cur.strip()
        if cur_stripped == "":
            if is_protected[j]:
                filtered_lines.append(cur)
                filtered_protected.append(True)
                continue
            # Preserve at most one enter before a function or class definition
            if is_python_function_ahead(raw_lines, j + 1):
                prev_raw = ""
                for k in range(len(filtered_lines) - 1, -1, -1):
                    s = filtered_lines[k].strip()
                    if s != "":
                        prev_raw = s
                        break
                if (
                    len(filtered_lines) == 0
                    or filtered_lines[-1].strip() == ""
                    or prev_raw.startswith(("@", "#"))
                ):
                    blanks_removed += 1
                    continue
                else:
                    filtered_lines.append(cur)
                    filtered_protected.append(False)
                    continue
            blanks_removed += 1
            continue
        filtered_lines.append(cur)
        filtered_protected.append(is_protected[j])
    collapsed_lines: list[str] = []
    k = 0
    total = len(filtered_lines)
    while k < total:
        line_a = filtered_lines[k]
        prot_a = filtered_protected[k]
        strip_a = line_a.strip()
        is_header = (
            not prot_a
            and strip_a.endswith(":")
            and not strip_a.startswith("#")
            and (
                strip_a.startswith(("def ", "async def ", "class "))
                or re.match(r"^(if|elif|else|while|for|with)\b", strip_a)
            )
        )
        if is_header and k + 1 < total and not filtered_protected[k + 1]:
            line_b = filtered_lines[k + 1]
            strip_b = line_b.strip()
            indent_a = len(line_a) - len(line_a.lstrip())
            indent_b = len(line_b) - len(line_b.lstrip())
            if indent_b > indent_a:
                has_more_in_block = False
                if k + 2 < total:
                    indent_c = len(filtered_lines[k + 2]) - len(filtered_lines[k + 2].lstrip())
                    if indent_c > indent_a:
                        has_more_in_block = True
                if not has_more_in_block:
                    can_collapse = (
                        strip_b in ("pass", "...")
                        or (
                            strip_b.startswith(("return ", "raise ", "pass", "...", "yield "))
                            and not strip_b.endswith(":")
                            and "#" not in strip_b
                        )
                    )
                    if can_collapse:
                        candidate = f"{line_a.rstrip()} {strip_b}"
                        collapsed_lines.append(candidate)
                        k += 2
                        continue
        collapsed_lines.append(line_a)
        k += 1
    newline = "\r\n" if has_crlf else "\n"
    candidate_result = newline.join(collapsed_lines) + newline
    try:
        ast.parse(candidate_result, filename=filename)
        final_result = candidate_result
    except SyntaxError:
        final_result = newline.join(filtered_lines) + newline
    return final_result, blanks_removed

def format_markup_source(content: str, filename: str = "") -> tuple[str, int]:
    """
    Condenses XML/HTML/XAML files:
    1. Drops redundant blank lines between tags.
    2. Collapses empty tags (<Tag>\n</Tag> -> <Tag></Tag>).
    """
    has_crlf = "\r\n" in content
    normalized = content.replace("\r\n", "\n").replace("\u00a0", " ")
    raw_lines = normalized.split("\n")
    is_protected = [False] * len(raw_lines)
    in_comment = False
    for idx, line in enumerate(raw_lines):
        if in_comment:
            is_protected[idx] = True
        if "<!--" in line and "-->" not in line:
            in_comment = True
            is_protected[idx] = True
        elif "-->" in line and in_comment:
            in_comment = False
    filtered_lines: list[str] = []
    blanks_removed = 0
    for j, line in enumerate(raw_lines):
        if line.strip() == "":
            if not is_protected[j]:
                blanks_removed += 1
                continue
        filtered_lines.append(line)
    collapsed_lines: list[str] = []
    k = 0
    total = len(filtered_lines)
    while k < total:
        line_a = filtered_lines[k]
        if k + 1 < total:
            line_b = filtered_lines[k + 1]
            strip_a = line_a.strip()
            strip_b = line_b.strip()
            m_open = re.match(r"^<([a-zA-Z0-9_\-\.:]+)(\s[^>]*)?>$", strip_a)
            if m_open:
                tag_name = m_open.group(1)
                if strip_b == f"</{tag_name}>":
                    collapsed_lines.append(line_a.rstrip() + strip_b)
                    k += 2
                    continue
        collapsed_lines.append(line_a)
        k += 1
    newline = "\r\n" if has_crlf else "\n"
    result = newline.join(collapsed_lines) + newline
    return result, blanks_removed

def format_generic_source(content: str, filename: str = "") -> tuple[str, int]:
    """Condenses generic script/config files by collapsing consecutive blank lines."""
    has_crlf = "\r\n" in content
    normalized = content.replace("\r\n", "\n").replace("\u00a0", " ")
    raw_lines = normalized.split("\n")
    filtered_lines: list[str] = []
    blanks_removed = 0
    for line in raw_lines:
        if line.strip() == "":
            if not filtered_lines or filtered_lines[-1].strip() == "":
                blanks_removed += 1
                continue
        filtered_lines.append(line)
    while filtered_lines and filtered_lines[-1].strip() == "":
        filtered_lines.pop()
        blanks_removed += 1
    newline = "\r\n" if has_crlf else "\n"
    result = newline.join(filtered_lines) + newline
    return result, blanks_removed

def strip_brace_comments(text: str) -> str:
    """Strips // and /* */ comments while protecting string and char literals."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        ch = text[i]
        if ch == '"' and i + 2 < n and text[i : i + 3] == '"""':
            q_count = 3
            while i + q_count < n and text[i + q_count] == '"':
                q_count += 1
            q_str = text[i : i + q_count]
            out.append(q_str)
            i += q_count
            while i < n:
                if text[i : i + q_count] == q_str:
                    out.append(q_str)
                    i += q_count
                    break
                out.append(text[i])
                i += 1
            continue
        if (ch == "@" and i + 1 < n and text[i + 1] == '"') or (
            i + 2 < n and text[i : i + 2] in ("$@", "@$") and text[i + 2] == '"'
        ):
            p_len = 3 if text[i] in ("$", "@") and text[i + 1] in ("$", "@") else 2
            out.append(text[i : i + p_len])
            i += p_len
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out.append('""')
                        i += 2
                        continue
                    out.append('"')
                    i += 1
                    break
                out.append(text[i])
                i += 1
            continue
        if ch == '"':
            out.append('"')
            i += 1
            while i < n:
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if c == '"':
                    i += 1
                    break
                i += 1
            continue
        if ch == "'":
            out.append("'")
            i += 1
            while i < n:
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if c == "'":
                    i += 1
                    break
                i += 1
            continue
        if ch == "`":
            out.append("`")
            i += 1
            while i < n:
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if c == "`":
                    i += 1
                    break
                i += 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += 2
            while i < n and text[i] not in ("\r", "\n"):
                i += 1
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            has_newline = False
            while i < n:
                if text[i] == "\n":
                    has_newline = True
                if text[i] == "*" and i + 1 < n and text[i + 1] == "/":
                    i += 2
                    break
                i += 1
            if has_newline:
                while out and out[-1] in (" ", "\t"):
                    out.pop()
                out.append("\n")
            else:
                if out and not out[-1].isspace():
                    out.append(" ")
            continue
        if ch == "\n":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            out.append("\n")
            i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)

def strip_python_comments(text: str) -> str:
    """Strips # comments while preserving docstrings and string literals."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        ch = text[i]
        if (ch == '"' and i + 2 < n and text[i : i + 3] == '"""') or (
            ch == "'" and i + 2 < n and text[i : i + 3] == "'''"
        ):
            q = text[i : i + 3]
            out.append(q)
            i += 3
            while i < n:
                if text[i : i + 3] == q:
                    out.append(q)
                    i += 3
                    break
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                i += 1
            continue
        if ch in ('"', "'"):
            q = ch
            out.append(q)
            i += 1
            while i < n:
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if c == q:
                    i += 1
                    break
                i += 1
            continue
        if ch == "#":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += 1
            while i < n and text[i] not in ("\r", "\n"):
                i += 1
            continue
        if ch == "\n":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            out.append("\n")
            i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)

def strip_markup_comments(text: str) -> str:
    """Strips <!-- -->, @* *@, and {# #} comments from markup files."""
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        if text[i : i + 4] == "<!--":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += 4
            while i < n and text[i : i + 3] != "-->":
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            if i < n:
                i += 3
            continue
        if text[i : i + 2] == "@*":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += 2
            while i < n and text[i : i + 2] != "*@":
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            if i < n:
                i += 2
            continue
        if text[i : i + 2] == "{#":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += 2
            while i < n and text[i : i + 2] != "#}":
                if text[i] == "\n":
                    out.append("\n")
                i += 1
            if i < n:
                i += 2
            continue
        if text[i] == "\n":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            out.append("\n")
            i += 1
            continue
        out.append(text[i])
        i += 1
    return "".join(out)

def strip_generic_comments(text: str, ext: str) -> str:
    """Strips comment markers from config and scripting files respecting quotes."""
    prefix = "#"
    if ext in (".sql", ".lua", ".hs", ".lhs", ".elm", ".purs", ".adb", ".ads", ".vhd", ".vhdl", ".applescript", ".e"):
        prefix = "--"
    elif ext in (".asm", ".s", ".inc", ".a51", ".lisp", ".lsp", ".cl", ".clj", ".cljs", ".cljc", ".edn", ".scm", ".ss", ".rkt", ".el", ".ahk", ".au3", ".inf"):
        prefix = ";"
    elif ext in (".tex", ".sty", ".cls", ".dtx", ".ins", ".erl", ".hrl", ".pro", ".prolog", ".ps", ".eps"):
        prefix = "%"
    elif ext in (".vb", ".vbs", ".bas", ".frm"):
        prefix = "'"
    elif ext in (".vim", ".vimrc"):
        prefix = '"'
    out: list[str] = []
    i, n = 0, len(text)
    p_len = len(prefix)
    while i < n:
        ch = text[i]
        if ch in ('"', "'") and prefix not in ('"', "'"):
            q = ch
            out.append(q)
            i += 1
            while i < n:
                c = text[i]
                out.append(c)
                if c == "\\" and i + 1 < n:
                    out.append(text[i + 1])
                    i += 2
                    continue
                if c == q:
                    i += 1
                    break
                i += 1
            continue
        if text[i : i + p_len] == prefix:
            while out and out[-1] in (" ", "\t"):
                out.pop()
            i += p_len
            while i < n and text[i] not in ("\r", "\n"):
                i += 1
            continue
        if ch == "\n":
            while out and out[-1] in (" ", "\t"):
                out.pop()
            out.append("\n")
            i += 1
            continue
        out.append(ch)
        i += 1
    return "".join(out)

def strip_comments(content: str, ext: str) -> str:
    """Dispatches comment stripping according to file type."""
    ext_lower = ext.lower()
    if ext_lower in BRACE_EXTENSIONS: return strip_brace_comments(content)
    elif ext_lower in PYTHON_EXTENSIONS: return strip_python_comments(content)
    elif ext_lower in MARKUP_EXTENSIONS: return strip_markup_comments(content)
    return strip_generic_comments(content, ext_lower)

def process_directory(target_path: Path, remove_bak: bool = False, remove_comments: bool = False):  
    if target_path.is_file():
        files = [] if is_ignored_file(target_path) else [target_path]
    else:
        files = []
        for root, dirs, filenames in os.walk(target_path):
            dirs[:] = [d for d in dirs if d not in IGNORED_DIRS]
            for file in filenames:
                if not file.endswith(".bak"):
                    file_p = Path(root) / file
                    if not is_ignored_file(file_p):
                        files.append(file_p)
    print(f"Scanning {len(files)} file(s) in: {target_path.resolve()}\n")
    modified_count = 0
    total_braces = 0
    total_blanks = 0
    for file_path in files:
        rel_path = (
            file_path.relative_to(target_path).as_posix()
            if target_path.is_dir()
            else file_path.name
        )
        ext = file_path.suffix.lower()
        if ext not in COMMENT_TEMPLATES:
            print(f"  [SKIPPED] Unsupported file type '{ext or file_path.name}': {rel_path}")
            continue
        try:
            with open(file_path, "rb") as f:
                raw_bytes = f.read()
            has_bom = raw_bytes.startswith(b"\xef\xbb\xbf")
            encoding = "utf-8-sig" if has_bom else "utf-8"
            original = raw_bytes.decode(encoding, errors="replace")
            braces = 0
            blanks = 0
            working_content = strip_comments(original, ext) if remove_comments else original
            comments_removed = remove_comments and (working_content != original)
            if ext in BRACE_EXTENSIONS:
                formatted, braces, blanks = format_brace_source(working_content, file_path.name, ext)
            elif ext in PYTHON_EXTENSIONS:
                formatted, blanks = format_python_source(working_content, file_path.name)
            elif ext in MARKUP_EXTENSIONS:
                formatted, blanks = format_markup_source(working_content, file_path.name)
            else:
                formatted, blanks = format_generic_source(working_content, file_path.name)
            if not remove_comments:
                formatted, header_added = apply_header_comment(formatted, rel_path, ext)
            else:
                header_added = False
            if formatted != original:
                backup_path = file_path.with_suffix(file_path.suffix + ".bak")
                shutil.copy2(file_path, backup_path)
                with open(file_path, "w", encoding=encoding, newline="") as f:
                    f.write(formatted)
                if remove_bak and backup_path.exists():
                    backup_path.unlink()
                modified_count += 1
                total_braces += braces
                total_blanks += blanks
                details = []
                if comments_removed:
                    details.append("Stripped comments")
                if header_added:
                    details.append("Added path header")
                if braces > 0:
                    details.append(f"Attached {braces} brace(s)")
                if blanks > 0:
                    details.append(f"Removed {blanks} blank line(s)")
                info = f" ({', '.join(details)})" if details else ""
                print(f"  [OK] {rel_path}{info}")
        except Exception as ex:
            print(f"  [SKIPPED/ERROR] {file_path.name}: {ex}")
    # Remove any pre-existing or lingering .bak files across the target path
    if remove_bak:
        removed_bak_count = 0
        search_dir = target_path if target_path.is_dir() else target_path.parent
        for root, dirs, filenames in os.walk(search_dir):
            dirs[:] = [d for d in dirs if d not in IGNORED_DIRS]
            for file in filenames:
                if file.endswith(".bak"):
                    bak_file = Path(root) / file
                    bak_file.unlink()
                    removed_bak_count += 1
        if removed_bak_count > 0:
            print(f"  [CLEANUP] Removed {removed_bak_count} *.bak file(s).")
    print(f"\nCompleted: {modified_count} file(s) updated safely.")
if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Safely format files and add relative path header comments."
    )
    parser.add_argument(
        "path",
        nargs="?",
        default=".",
        help="Target file or project directory (default: current directory)",
    )
    parser.add_argument(
        "--remove-bak",
        action="store_true",
        help="Remove all *.bak backup files after fixing braces",
    )
    parser.add_argument(
        "--remove-comments",
        action="store_true",
        help="Strip all comments from files without modifying real code",
    )
    args = parser.parse_args()
    process_directory(
        Path(args.path),
        remove_bak=args.remove_bak,
        remove_comments=args.remove_comments,
    )
