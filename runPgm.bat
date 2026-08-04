;
jjflexible.exe
setlocal
set fn=C:\Users\jjs\appdata\roaming\JJFlexRadio\jjflexradioboottrace.txt
set outfn=t.txt
grep -i "Read(" %fn% | grep "exception:" >%outfn%
grep -i "propertyChangedHandler exception:" %fn% >>%outfn%
"C:\Program Files (x86)\TextPad 4\textpad" %outfn%
endlocal
