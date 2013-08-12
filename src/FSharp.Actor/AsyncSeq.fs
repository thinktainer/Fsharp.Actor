﻿namespace FSharp.Actor

open System
/// Union type that represents different messages that can be sent to the
/// IObserver interface. The IObserver type is equivalent to a type that has
/// just OnNext method that gets 'ObservableUpdate' as an argument.
type ObservableUpdate<'T> = 
  | Next of 'T
  | Error of exn
  | Completed

module Observable =

  /// Creates an observable that calls the specified function (each time)
  /// after an observer is attached to the observable. This is useful to 
  /// make sure that events triggered by the function are handled. 
  let guard f (e:IObservable<'Args>) =  
    { new IObservable<'Args> with  
        member x.Subscribe(observer) =  
          let rm = e.Subscribe(observer) in f(); rm } 

  /// Turns observable into an observable that only calls OnNext method of the
  /// observer, but gives it a discriminated union that represents different
  /// kinds of events (error, next, completed)
  let asUpdates (input:IObservable<'T>) = 
    { new IObservable<_> with
        member x.Subscribe(observer) =
          input.Subscribe
            ({ new IObserver<_> with
                member x.OnNext(v) = observer.OnNext(Next v)
                member x.OnCompleted() = observer.OnNext(Completed) 
                member x.OnError(e) = observer.OnNext(Error e) }) }

// ----------------------------------------------------------------------------

[<AutoOpen>]
module ObservableExtensions =

  /// Helper that can be used for writing CPS-style code that resumes
  /// on the same thread where the operation was started.
  let internal synchronize f = 
    let ctx = System.Threading.SynchronizationContext.Current 
    f (fun g ->
      let nctx = System.Threading.SynchronizationContext.Current 
      if ctx <> null && ctx <> nctx then ctx.Post((fun _ -> g()), null)
      else g() )

  type Microsoft.FSharp.Control.Async with 

    /// Behaves like AwaitObservable, but calls the specified guarding function
    /// after a subscriber is registered with the observable.
    static member GuardedAwaitObservable (ev1:IObservable<'T1>) guardFunction =
      synchronize (fun f ->
        Async.FromContinuations((fun (cont,econt,ccont) -> 
          let rec finish cont value = 
            remover.Dispose()
            f (fun () -> cont value)
          and remover : IDisposable = 
            ev1.Subscribe
              ({ new IObserver<_> with
                   member x.OnNext(v) = finish cont v
                   member x.OnError(e) = finish econt e
                   member x.OnCompleted() = 
                      let msg = "Cancelling the workflow, because the Observable awaited using AwaitObservable has completed."
                      finish ccont (new System.OperationCanceledException(msg)) }) 
          guardFunction() )))

    /// Creates an asynchronous workflow that will be resumed when the 
    /// specified observables produces a value. The workflow will return 
    /// the value produced by the observable.
    static member AwaitObservable(ev1:IObservable<'T1>) =
      synchronize (fun f ->
        Async.FromContinuations((fun (cont,econt,ccont) -> 
          let rec finish cont value = 
            remover.Dispose()
            f (fun () -> cont value)
          and remover : IDisposable = 
            ev1.Subscribe
              ({ new IObserver<_> with
                   member x.OnNext(v) = finish cont v
                   member x.OnError(e) = finish econt e
                   member x.OnCompleted() = 
                      let msg = "Cancelling the workflow, because the Observable awaited using AwaitObservable has completed."
                      finish ccont (new System.OperationCanceledException(msg)) }) 
          () )))
  
    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified two observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable(ev1:IObservable<'T1>, ev2:IObservable<'T2>) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of2 
          ev2 |> Observable.map Choice2Of2 ] 
      |> Async.AwaitObservable

    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified three observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable
        ( ev1:IObservable<'T1>, ev2:IObservable<'T2>, ev3:IObservable<'T3> ) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of3 
          ev2 |> Observable.map Choice2Of3
          ev3 |> Observable.map Choice3Of3 ] 
      |> Async.AwaitObservable

    /// Creates an asynchronous workflow that will be resumed when the 
    /// first of the specified four observables produces a value. The 
    /// workflow will return a Choice value that can be used to identify
    /// the observable that produced the value.
    static member AwaitObservable( ev1:IObservable<'T1>, ev2:IObservable<'T2>, 
                                   ev3:IObservable<'T3>, ev4:IObservable<'T4> ) = 
      List.reduce Observable.merge 
        [ ev1 |> Observable.map Choice1Of4 
          ev2 |> Observable.map Choice2Of4
          ev3 |> Observable.map Choice3Of4
          ev4 |> Observable.map Choice4Of4 ] 
      |> Async.AwaitObservable

// ----------------------------------------------------------------------------

/// An asynchronous sequence represents a delayed computation that can be
/// started to produce either Cons value consisting of the next element of the 
/// sequence (head) together with the next asynchronous sequence (tail) or a 
/// special value representing the end of the sequence (Nil)
type AsyncSeq<'T> = Async<AsyncSeqInner<'T>> 

/// The interanl type that represents a value returned as a result of
/// evaluating a step of an asynchronous sequence
and AsyncSeqInner<'T> =
  | Nil
  | Cons of 'T * AsyncSeq<'T>


/// Module with helper functions for working with asynchronous sequences
module AsyncSeq = 

  /// Creates an empty asynchronou sequence that immediately ends
  [<GeneralizableValue>]
  let empty<'T> : AsyncSeq<'T> = 
    async { return Nil }
 
  /// Creates an asynchronous sequence that generates a single element and then ends
  let singleton (v:'T) : AsyncSeq<'T> = 
    async { return Cons(v, empty) }

  /// Yields all elements of the first asynchronous sequence and then 
  /// all elements of the second asynchronous sequence.
  let rec append (seq1: AsyncSeq<'T>) (seq2: AsyncSeq<'T>) : AsyncSeq<'T> = 
    async { let! v1 = seq1
            match v1 with 
            | Nil -> return! seq2
            | Cons (h,t) -> return Cons(h,append t seq2) }


  /// Computation builder that allows creating of asynchronous 
  /// sequences using the 'asyncSeq { ... }' syntax
  type AsyncSeqBuilder() =
    member x.Yield(v) = singleton v
    member x.YieldFrom(s) = s
    member x.Zero () = empty
    member x.Bind (inp:Async<'T>, body : 'T -> AsyncSeq<'U>) : AsyncSeq<'U> = 
      async.Bind(inp, body)
    member x.Combine (seq1:AsyncSeq<'T>,seq2:AsyncSeq<'T>) = 
      append seq1 seq2
    member x.While (gd, seq:AsyncSeq<'T>) = 
      if gd() then x.Combine(seq,x.Delay(fun () -> x.While (gd, seq))) else x.Zero()
    member x.Delay (f:unit -> AsyncSeq<'T>) = 
      async.Delay(f)

      
  /// Builds an asynchronou sequence using the computation builder syntax
  let asyncSeq = new AsyncSeqBuilder()

  /// Tries to get the next element of an asynchronous sequence
  /// and returns either the value or an exception
  let internal tryNext (input:AsyncSeq<_>) = async { 
    try 
      let! v = input
      return Choice1Of2 v
    with e -> 
      return Choice2Of2 e }

  /// Implements the 'TryWith' functionality for computation builder
  let rec internal tryWith (input : AsyncSeq<'T>) handler =  asyncSeq { 
    let! v = tryNext input
    match v with 
    | Choice1Of2 Nil -> ()
    | Choice1Of2 (Cons (h, t)) -> 
        yield h
        yield! tryWith t handler
    | Choice2Of2 rest -> 
        yield! handler rest }
 
  /// Implements the 'TryFinally' functionality for computation builder
  let rec internal tryFinally (input : AsyncSeq<'T>) compensation = asyncSeq {
    let! v = tryNext input
    match v with 
    | Choice1Of2 Nil -> 
        compensation()
    | Choice1Of2 (Cons (h, t)) -> 
        yield h
        yield! tryFinally t compensation
    | Choice2Of2 e -> 
        compensation()
        yield! raise e }

  /// Creates an asynchronou sequence that iterates over the given input sequence.
  /// For every input element, it calls the the specified function and iterates
  /// over all elements generated by that asynchronous sequence.
  /// This is the 'bind' operation of the computation expression (exposed using
  /// the 'for' keyword in asyncSeq computation).
  let rec collect f (input : AsyncSeq<'T>) : AsyncSeq<'TResult> = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons(h, t) ->
        yield! f h
        yield! collect f t }


  // Add additional methods to the 'asyncSeq' computation builder
  type AsyncSeqBuilder with
    member x.TryFinally (body: AsyncSeq<'T>, compensation) = 
      tryFinally body compensation   
    member x.TryWith (body: AsyncSeq<_>, handler: (exn -> AsyncSeq<_>)) = 
      tryWith body handler
    member x.Using (resource:#IDisposable, binder) = 
      tryFinally (binder resource) (fun () -> 
        if box resource <> null then resource.Dispose())

    /// For loop that iterates over a synchronous sequence (and generates
    /// all elements generated by the asynchronous body)
    member x.For(seq:seq<'T>, action:'T -> AsyncSeq<'TResult>) = 
      let enum = seq.GetEnumerator()
      x.TryFinally(x.While((fun () -> enum.MoveNext()), x.Delay(fun () -> 
        action enum.Current)), (fun () -> 
          if enum <> null then enum.Dispose() ))

    /// Asynchronous for loop - for all elements from the input sequence,
    /// generate all elements produced by the body (asynchronously). See
    /// also the AsyncSeq.collect function.
    member x.For (seq:AsyncSeq<'T>, action:'T -> AsyncSeq<'TResult>) = 
      collect action seq


  // Add asynchronous for loop to the 'async' computation builder
  type Microsoft.FSharp.Control.AsyncBuilder with
    member x.For (seq:AsyncSeq<'T>, action:'T -> Async<unit>) = 
      async.Bind(seq, function
        | Nil -> async.Zero()
        | Cons(h, t) -> async.Combine(action h, x.For(t, action)))

  // --------------------------------------------------------------------------
  // Additional combinators (implemented as async/asyncSeq computations)

  /// Builds a new asynchronous sequence whose elements are generated by 
  /// applying the specified function to all elements of the input sequence.
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let mapAsync f (input : AsyncSeq<'T>) : AsyncSeq<'TResult> = asyncSeq {
    for itm in input do 
      let! v = f itm
      yield v }

  /// Asynchronously iterates over the input sequence and generates 'x' for 
  /// every input element for which the specified asynchronous function 
  /// returned 'Some(x)' 
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let chooseAsync f (input : AsyncSeq<'T>) : AsyncSeq<'R> = asyncSeq {
    for itm in input do
      let! v = f itm
      match v with 
      | Some v -> yield v 
      | _ -> () }

  /// Builds a new asynchronous sequence whose elements are those from the
  /// input sequence for which the specified function returned true.
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let filterAsync f (input : AsyncSeq<'T>) = asyncSeq {
    for v in input do
      let! b = f v
      if b then yield v }

  /// Asynchronously returns the last element that was generated by the
  /// given asynchronous sequence (or the specified default value).
  let rec lastOrDefault def (input : AsyncSeq<'T>) = async {
    let! v = input
    match v with 
    | Nil -> return def
    | Cons(h, t) -> return! lastOrDefault h t }

  /// Asynchronously returns the first element that was generated by the
  /// given asynchronous sequence (or the specified default value).
  let firstOrDefault def (input : AsyncSeq<'T>) = async {
    let! v = input
    match v with 
    | Nil -> return def
    | Cons(h, _) -> return h }

  /// Aggregates the elements of the input asynchronous sequence using the
  /// specified 'aggregation' function. The result is an asynchronous 
  /// sequence of intermediate aggregation result.
  ///
  /// The aggregation function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec scanAsync f (state:'TState) (input : AsyncSeq<'T>) = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons(h, t) ->
        let! v = f state h
        yield v
        yield! t |> scanAsync f v }

  /// Iterates over the input sequence and calls the specified function for
  /// every value (to perform some side-effect asynchronously).
  ///
  /// The specified function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec iterAsync f (input : AsyncSeq<'T>) = async {
    for itm in input do 
      do! f itm }

  /// Returns an asynchronous sequence that returns pairs containing an element
  /// from the input sequence and its predecessor. Empty sequence is returned for
  /// singleton input sequence.
  let rec pairwise (input : AsyncSeq<'T>) = asyncSeq {
    let! v = input
    match v with
    | Nil -> ()
    | Cons(h, t) ->
        let prev = ref h
        for v in t do
          yield (!prev, v)
          prev := v }

  /// Aggregates the elements of the input asynchronous sequence using the
  /// specified 'aggregation' function. The result is an asynchronous 
  /// workflow that returns the final result.
  ///
  /// The aggregation function is asynchronous (and the input sequence will
  /// be asked for the next element after the processing of an element completes).
  let rec foldAsync f (state:'TState) (input : AsyncSeq<'T>) = 
    input |> scanAsync f state |> lastOrDefault state

  /// Same as AsyncSeq.foldAsync, but the specified function is synchronous
  /// and returns the result of aggregation immediately.
  let rec fold f (state:'TState) (input : AsyncSeq<'T>) = 
    foldAsync (fun st v -> f st v |> async.Return) state input 

  /// Same as AsyncSeq.scanAsync, but the specified function is synchronous
  /// and returns the result of aggregation immediately.
  let rec scan f (state:'TState) (input : AsyncSeq<'T>) = 
    scanAsync (fun st v -> f st v |> async.Return) state input 

  /// Same as AsyncSeq.mapAsync, but the specified function is synchronous
  /// and returns the result of projection immediately.
  let map f (input : AsyncSeq<'T>) = 
    mapAsync (f >> async.Return) input

  /// Same as AsyncSeq.iterAsync, but the specified function is synchronous
  /// and performs the side-effect immediately.
  let iter f (input : AsyncSeq<'T>) = 
    iterAsync (f >> async.Return) input

  /// Same as AsyncSeq.chooseAsync, but the specified function is synchronous
  /// and processes the input element immediately.
  let choose f (input : AsyncSeq<'T>) = 
    chooseAsync (f >> async.Return) input

  /// Same as AsyncSeq.filterAsync, but the specified predicate is synchronous
  /// and processes the input element immediately.
  let filter f (input : AsyncSeq<'T>) =
    filterAsync (f >> async.Return) input
    
  // --------------------------------------------------------------------------
  // Converting from/to synchronous sequences or IObservables

  /// Creates an asynchronous sequence that lazily takes element from an
  /// input synchronous sequence and returns them one-by-one.
  let ofSeq (input : seq<'T>) = asyncSeq {
    for el in input do 
      yield el }

  
  // --------------------------------------------------------------------------

  /// Combines two asynchronous sequences into a sequence of pairs. 
  /// The values from sequences are retrieved in parallel. 
  let rec zip (input1 : AsyncSeq<'T1>) (input2 : AsyncSeq<'T2>) : AsyncSeq<_> = async {
    let! ft = input1 |> Async.StartChild
    let! s = input2
    let! f = ft
    match f, s with 
    | Cons(hf, tf), Cons(hs, ts) ->
        return Cons( (hf, hs), zip tf ts)
    | _ -> return Nil }

  /// Returns elements from an asynchronous sequence while the specified 
  /// predicate holds. The predicate is evaluated asynchronously.
  let rec takeWhileAsync p (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    let! v = input
    match v with
    | Cons(h, t) -> 
        let! res = p h
        if res then 
          return Cons(h, takeWhileAsync p t)
        else return Nil
    | Nil -> return Nil }

  /// Skips elements from an asynchronous sequence while the specified 
  /// predicate holds and then returns the rest of the sequence. The 
  /// predicate is evaluated asynchronously.
  let rec skipWhileAsync p (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    let! v = input
    match v with
    | Cons(h, t) -> 
        let! res = p h
        if res then return! skipWhileAsync p t
        else return! t
    | Nil -> return Nil }

  /// Returns elements from an asynchronous sequence while the specified 
  /// predicate holds. The predicate is evaluated synchronously.
  let rec takeWhile p (input : AsyncSeq<'T>) = 
    takeWhileAsync (p >> async.Return) input

  /// Skips elements from an asynchronous sequence while the specified 
  /// predicate holds and then returns the rest of the sequence. The 
  /// predicate is evaluated asynchronously.
  let rec skipWhile p (input : AsyncSeq<'T>) = 
    skipWhileAsync (p >> async.Return) input

  /// Returns the first N elements of an asynchronous sequence
  let rec take count (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    if count > 0 then
      let! v = input
      match v with
      | Cons(h, t) -> 
          return Cons(h, take (count - 1) t)
      | Nil -> return Nil 
    else return Nil }

  /// Skips the first N elements of an asynchronous sequence and
  /// then returns the rest of the sequence unmodified.
  let rec skip count (input : AsyncSeq<'T>) : AsyncSeq<_> = async {
    if count > 0 then
      let! v = input
      match v with
      | Cons(h, t) -> 
          return Cons(h, skip (count - 1) t)
      | Nil -> return Nil 
    else return! input }


[<AutoOpen>]
module AsyncSeqExtensions = 
  /// Builds an asynchronou sequence using the computation builder syntax
  let asyncSeq = new AsyncSeq.AsyncSeqBuilder()

  // Add asynchronous for loop to the 'async' computation builder
  type Microsoft.FSharp.Control.AsyncBuilder with
    member x.For (seq:AsyncSeq<'T>, action:'T -> Async<unit>) = 
      async.Bind(seq, function
        | Nil -> async.Zero()
        | Cons(h, t) -> async.Combine(action h, x.For(t, action)))
