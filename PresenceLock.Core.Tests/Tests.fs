module PresenceLock.Core.Tests.Tests

open Xunit
open FsCheck.Xunit
open PresenceLock.Core

[<Fact>]
let ``reverseTwice returns the original list unchanged`` () =
    Assert.Equal<int list>([ 1; 2; 3 ], Library.reverseTwice [ 1; 2; 3 ])

[<Property>]
let ``reverseTwice is involutive for any int list`` (xs: int list) =
    Library.reverseTwice xs = xs
