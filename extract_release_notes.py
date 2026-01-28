import sys
from pathlib import Path
pdf_path = Path('SmartSDR-v4.0.1-Release-Notes.pdf')
try:
    from PyPDF2 import PdfReader
except ImportError:
    sys.stderr.write('PyPDF2 not installed\n')
    sys.exit(1)
reader = PdfReader(str(pdf_path))
text = '\n'.join((page.extract_text() or '') for page in reader.pages)
print(text[:6000])
