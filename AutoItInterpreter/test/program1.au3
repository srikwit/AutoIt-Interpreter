﻿#include-once
#include <APIComConstants.au3>
#include 'header-1.au3'


#cs
    Func f2()
        // this is an invalid comment inside an non-existent function ...
    EndFunc
#ce

Func f1()
    If true Then
        $test = 0x7fffffffffffffff
        $test = 0x80000000000000000 ; <-- too big  *wink*
    Else
        If $test Then
            f2()
        else ; comment
            $test = "test"
        endif
    EndIf
EndFunc
; comment
Func f2()
    if $foo then
        bar("top", "kek", 0x1488, "/blubb/")
    else
        baz(42)
    endif
EndFunc

for $cnt1 = 0 to 7
    if "te""st" <> 5 then
        f2()
    endif

    for $cnt2 = 17 to -6 step -2
        switch $cnt2
            case 8, 0x10, 2
            case 1 to "3"
            case 0o7
                continuecase
            case 2 to 5, $cnt2 to "7", 8, "6" to -5
            case else
                f2()
            case else ; <--- should not be valid !
                f2()
        endswitch
    next

     $test[5] = { 0, 1, 2, 3, 4 }

    for $var in $test
        printf($var)
    next
next