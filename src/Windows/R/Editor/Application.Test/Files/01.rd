﻿\title{Miscellaneous Mathematical Functions}\name{MathFun}\alias{abs}\alias{sqrt}\keyword{math}\description{
  \code{abs(x)} computes the absolute value of x, \code{sqrt(x)} computes the
  (principal) square root of x, \eqn{\sqrt{x}}.% Details for complex x are below

  The naming follows the standard for computer languages such as C or Fortran.
}\usage{
abs(x)
sqrt(x)
}\arguments{
  \item{x}{a numeric or \code{\link{complex}} vector or array.}
}\details{
  These are \link{internal generic} \link{primitive} functions: methods
  can be defined for them individually or via the
  \code{\link[=S3groupGeneric]{Math}} group generic.  For complex
  arguments (and the default method), \code{z}, \code{abs(z) ==
  \link{Mod}(z)} and \code{sqrt(z) == z^0.5}.

  \code{abs(x)} returns an \code{\link{integer}} vector when \code{x} is
  \code{integer} or \code{\link{logical}}.
}\section{S4 methods}{
  Both are S4 generic and members of the
  \code{\link[=S4groupGeneric]{Math}} group generic.
}\references{
  Becker, R. A., Chambers, J. M. and Wilks, A. R. (1988)
  \emph{The New S Language}.
  Wadsworth & Brooks/Cole.
}\seealso{
  \code{\link{Arithmetic}} for simple, \code{\link{log}} for logarithmic,
  \code{\link{sin}} for trigonometric, and \code{\link{Special}} for
  special mathematical functions.

  \sQuote{\link{plotmath}} for the use of \code{sqrt} in plot annotation.
}\examples{
require(stats) # for spline
require(graphics)
xx <- -9:9
plot(xx, sqrt(abs(xx)),  col = "red")
lines(spline(xx, sqrt(abs(xx)), n=101), col = "pink")
