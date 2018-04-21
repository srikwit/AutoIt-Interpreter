﻿namespace AutoItExpressionParser

open Piglet.Parser.Configuration
open Piglet.Parser

[<AutoOpen>]
module Util =
    let internal (+>) (o : obj[]) n = unbox o.[n]

    let (|As|_|) (p:'T) : 'U option =
        let p = p :> obj
        if p :? 'U then Some (p :?> 'U) else None

type OperatorAssociativity = 
    | Left
    | Right

type ProductionWrapperBase (p : IProduction<obj>) =
    member x.Production = p
    member x.SetReduceToFirst () = p.SetReduceToFirst()
    member x.SetPrecedence(precedenceGroup) = p.SetPrecedence(precedenceGroup)

type ProductionWrapper<'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : (unit -> 'T)) =
        p.SetReduceFunction (fun o -> box (f ()))

type ProductionWrapper<'a,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'T)) =
        p.SetReduceFunction (fun o -> o+>0
                                      |> f
                                      |> box)

type ProductionWrapper<'a,'b,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1)))

type ProductionWrapper<'a,'b,'c,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2)))

type ProductionWrapper<'a,'b,'c,'d,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3)))

type ProductionWrapper<'a,'b,'c,'d,'e,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4)))

type ProductionWrapper<'a,'b,'c,'d,'e,'f,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5)))

type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6)))

type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6) (o+>7)))

type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'i -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6) (o+>7) (o+>8)))

type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'i -> 'j -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6) (o+>7) (o+>8)))
        
type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'k,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'i -> 'j -> 'k -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6) (o+>7) (o+>8) (o+>9)))
        
type ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'k,'l,'T> (p : IProduction<obj>) =
    inherit ProductionWrapperBase(p)
    member x.SetReduceFunction (f : ('a -> 'b -> 'c -> 'd -> 'e -> 'f -> 'g -> 'h -> 'i -> 'j -> 'k -> 'l -> 'T)) =
        p.SetReduceFunction (fun o -> box (f (o+>0) (o+>1) (o+>2) (o+>3) (o+>4) (o+>5) (o+>6) (o+>7) (o+>8) (o+>9) (o+>10)))

type SymbolWrapper<'T> (symbol : ISymbol<obj>) =
    member x.Symbol = symbol

type TerminalWrapper<'T> (terminal : ITerminal<obj>) =
    inherit SymbolWrapper<'T>(terminal)

type NonTerminalWrapper<'T> (nonTerminal : INonTerminal<obj>) =
    inherit SymbolWrapper<'T>(nonTerminal)
    
    let (!>) (p : SymbolWrapper<'a>) = p.Symbol;

    member x.AddProduction () =
        nonTerminal.AddProduction()
        |> ProductionWrapper<'T>
        
    member x.AddProduction p = nonTerminal.AddProduction !>p
                               |> ProductionWrapper<'a,'T>
        
    member x.AddProduction (p1, p2) =
        nonTerminal.AddProduction(!>p1, !>p2)
        |> ProductionWrapper<'a,'b,'T>
        
    member x.AddProduction (p1, p2, p3) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3)
        |> ProductionWrapper<'a,'b,'c,'T>
        
    member x.AddProduction (p1, p2, p3, p4) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4)
        |> ProductionWrapper<'a,'b,'c,'d,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7, p8) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7, !>p8)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'T>

    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7, p8, p9) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7, !>p8, !>p9)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7, p8, p9, p10) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7, !>p8, !>p9, !>p10)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7, !>p8, !>p9, !>p10, !>p11)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'k,'T>
        
    member x.AddProduction (p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12) =
        nonTerminal.AddProduction(!>p1, !>p2, !>p3, !>p4, !>p5, !>p6, !>p7, !>p8, !>p9, !>p10, !>p11, !>p12)
        |> ProductionWrapper<'a,'b,'c,'d,'e,'f,'g,'h,'i,'j,'k,'l,'T>

[<AbstractClass>]
type AbstractParser<'a>() as this =
    let config = ParserFactory.Configure<obj>()
    let mutable parser : IParser<obj> = null
    member x.Configuration with get() = config
    member internal x.t = x.Configuration.CreateTerminal >> TerminalWrapper<string>
    member internal x.tf<'T> regex (onParse : (string -> 'T)) = TerminalWrapper<'T>(x.Configuration.CreateTerminal(regex, (fun s -> box (onParse s))))
    member internal x.nt<'T>() = NonTerminalWrapper<'T>(x.Configuration.CreateNonTerminal())
    member internal x.a d s =
        let arg = List.map (fun (f : SymbolWrapper<_>) -> downcast f.Symbol)
               >> List.toArray
        match d with
        | Left -> x.Configuration.LeftAssociative(arg s)
        | Right -> x.Configuration.RightAssociative(arg s)
        |> ignore
    abstract BuildParser : unit -> unit
    member x.Initialize() =
        x.BuildParser()
        parser <- (x.Configuration : IParserConfigurator<obj>).CreateParser()
    member x.Parse (s : string) =
        if parser = null then
            x.Initialize()
        parser.Parse(s.Replace('\t', ' ')) :?> 'a
            
[<AutoOpen>]
module ProductionUtil =
    let internal reducef (s : NonTerminalWrapper<'a>) x = s.AddProduction().SetReduceFunction x
    let internal reduce0 (s : NonTerminalWrapper<'a>) a = s.AddProduction(a).SetReduceToFirst()
    let internal reduce1 (s : NonTerminalWrapper<'a>) a x = s.AddProduction(a).SetReduceFunction x
    let internal reduce2 (s : NonTerminalWrapper<'a>) a b x = s.AddProduction(a, b).SetReduceFunction x
    let internal reduce3 (s : NonTerminalWrapper<'a>) a b c x = s.AddProduction(a, b, c).SetReduceFunction x
    let internal reduce4 (s : NonTerminalWrapper<'a>) a b c d x = s.AddProduction(a, b, c, d).SetReduceFunction x
    let internal reduce5 (s : NonTerminalWrapper<'a>) a b c d e x = s.AddProduction(a, b, c, d, e).SetReduceFunction x
    let internal reduce6 (s : NonTerminalWrapper<'a>) a b c d e f x = s.AddProduction(a, b, c, d, e, f).SetReduceFunction x
    let internal reduce7 (s : NonTerminalWrapper<'a>) a b c d e f g x = s.AddProduction(a, b, c, d, e, f, g).SetReduceFunction x
    let internal reduce8 (s : NonTerminalWrapper<'a>) a b c d e f g h x = s.AddProduction(a, b, c, d, e, f, g, h).SetReduceFunction x
    let internal reduce9 (s : NonTerminalWrapper<'a>) a b c d e f g h i x = s.AddProduction(a, b, c, d, e, f, g, h, i).SetReduceFunction x
    let internal reduce10 (s : NonTerminalWrapper<'a>) a b c d e f g h i j x = s.AddProduction(a, b, c, d, e, f, g, h, i, j).SetReduceFunction x
    let internal reduce11 (s : NonTerminalWrapper<'a>) a b c d e f g h i j k x = s.AddProduction(a, b, c, d, e, f, g, h, i, j, k).SetReduceFunction x
    let internal reduce12 (s : NonTerminalWrapper<'a>) a b c d e f g h i j k l x = s.AddProduction(a, b, c, d, e, f, g, h, i, j, k, l).SetReduceFunction x
