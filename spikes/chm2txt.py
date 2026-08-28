import html.parser, pathlib, re, sys

class T(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.out=[]; self.skip=0
    def handle_starttag(self,tag,attrs):
        if tag in ('script','style'): self.skip+=1
        if tag in ('p','br','div','tr','li','h1','h2','h3','h4','table'): self.out.append('\n')
    def handle_endtag(self,tag):
        if tag in ('script','style'): self.skip=max(0,self.skip-1)
    def handle_data(self,d):
        if not self.skip: self.out.append(d)

def totext(p):
    t=T(); t.feed(pathlib.Path(p).read_text(encoding='utf-8',errors='replace'))
    txt=''.join(t.out)
    txt=re.sub(r'[ \t]+',' ',txt)
    txt=re.sub(r'\n\s*\n+','\n',txt)
    return txt.strip()

for f in sys.argv[1:]:
    print('='*12, f, '='*12)
    sys.stdout.buffer.write(totext(f).encode("utf-8",errors="replace")); print()
