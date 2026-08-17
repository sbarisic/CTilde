# C~ language reference

## Document status

This document describes the standalone CTilde repository. It records the syntax that the current parser accepts.

Parser support does not imply correct FishAsm output. [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md) identifies incomplete and incorrect behavior.

C~ resembles C, but it is not compatible with C. Some declarations use different syntax, and many C operators are absent.

## Source files

The compiler does not require a file extension. The included examples use `.ct` and `.c`.

A source file contains a sequence of declarations. The parser does not support include files, imports, namespaces, or separate compilation.

## Lexical rules

### Identifiers

An identifier starts with a letter or underscore. Later characters can include letters, digits, and underscores.

Identifiers are case-sensitive. Type names also use identifier tokens.

### Keywords

The tokenizer defines these keywords:

```text
class  __ctor  __dtor  if  else  while  true  false
naked  break   static  return    continue
```

Built-in type names are not keywords. The parser reads them as identifiers.

### Comments

C~ accepts line comments and block comments.

```c
// line comment

/* block comment */
```

Nested block comments are not supported.

### Literals

The parser accepts unsigned decimal integer text, decimal text, character literals, string literals, `true`, and `false`.

```c
42
1.5
'A'
'\n'
"hello\n"
true
false
```

Character literals support `\n`, `\r`, `\t`, `\b`, `\'`, `\"`, and `\\`.

String decoding supports `\n`, `\t`, `\"`, and `\\`. The parser does not decode `\r` or `\b` in strings.

The FishAsm backend only compiles integer literals correctly. Decimal literals fail during code generation.

### Symbols

The tokenizer defines these symbols:

```text
( ) { } [ ] , * ; == != = + - > >= < <= & ++ --
```

The tokenizer does not define `/`, `%`, `.`, `!`, `&&`, `||`, shifts, or bitwise operators.

## Types

The compiler lists these built-in types:

| Type | Intended size | Notes |
| --- | ---: | --- |
| `bool` | 1 byte | Unsigned storage |
| `byte` | 1 byte | Unsigned storage |
| `char` | 1 byte | Signed loads in parts of the backend |
| `int` | 4 bytes | Signed integer |
| `uint` | 4 bytes | Unsigned integer |
| `float` | 4 bytes | Listed but not implemented end to end |
| `string` | 4 bytes | Treated as a byte pointer |
| `void` | 0 bytes | Valid as a function return type |

The parser accepts any identifier as a type name. The FishAsm backend rejects unknown non-pointer types when it needs their size.

### Type syntax

```text
type := identifier
      | identifier "*"
      | identifier "[]"
      | identifier "[" integer "]"
```

Only one pointer or array suffix is accepted. Multiple pointer levels are not supported.

The type parser places an array suffix on the type, not the variable name.

```c
void read(int[4] values) {  // accepted parameter syntax
}

int[4] values;              // rejected by statement look-ahead
int values[4];              // rejected C syntax
```

Variable statement look-ahead does not recognize either array declaration. Static string arrays are the only implemented array allocation form.

## Variables

### Declaration

```c
int count;
uint total = 0;
string text;
```

A declaration can appear at module or block scope. The compiler does not implement lexical block scopes or variable shadowing.

### Assignment

```c
count = 3;
text[index] = 'A';
```

Simple assignment supports an identifier on the left side. Indexed assignment supports an indexed identifier.

Dereference assignment and chained assignment are not implemented.

### Static storage

The parser accepts `static` as an expression that contains a type.

```c
string buffer = static string[50];
```

The FishAsm backend allocates storage only for `static string[N]`. Other static types emit an empty label.

### Single-assignment variables

The original TODO proposes a variable attribute that permits one runtime assignment. The parser and backends do not implement this feature.

## Functions

### Definition

```c
uint add(uint left, uint right) {
	return left + right;
}
```

Each parameter requires a type and a name. `function(void)` is not supported. Use an empty parameter list for a function with no parameters.

```c
void run() {
}
```

### Declaration without a body

The parser accepts a semicolon instead of a function body.

```c
void external_call(uint value);
```

The FishAsm backend records the global name but does not emit a function body. The C backend does not handle this form safely.

### Calls

Function calls are valid as statements.

```c
print("hello");
add(2, 3);
```

Calls are not valid primary expressions. This declaration fails to parse:

```c
uint result = add(2, 3);
```

The FishAsm backend returns scalar function results in `EAX`. The parser prevents normal source code from using that result.

The current caller pushes arguments in source order. This reverses parameters under the backend stack convention.

### Return

```c
return;
return value;
```

The compiler does not compare the returned expression type with the declared return type. It also emits an implicit return after the function body.

### Naked functions

The `naked` keyword disables the normal function prologue and implicit epilogue.

```c
naked void entry() {
	__asm("RET");
}
```

An explicit C~ `return` still emits `LEAVE` and `RET`. Do not use it in a naked function.

### Special calls

`__asm` emits a string literal directly into the FishAsm output.

```c
__asm("DBG_BREAK");
```

Only string literal arguments are accepted.

`syscall_2` emits the special two-argument Fishmachine syscall instruction.

```c
syscall_2(1, character);
syscall_2(2, number);
```

The first argument must be an integer literal. The backend does not validate this requirement before it casts the expression.

Function names that start with `handler_` receive a special interrupt wrapper. This rule is a naming convention, not a language attribute.

## Expressions

The parser supports these primary forms:

- An identifier
- An integer or decimal literal
- A character or string literal
- `true` or `false`
- An indexed identifier such as `items[index]`
- A parenthesized expression
- An address expression such as `&name`
- A dereference expression such as `*pointer`
- A static allocation expression

The parser supports addition, subtraction, and six comparison operators.

```c
left + right
left - right
left == right
left != right
left < right
left <= right
left > right
left >= right
```

The parser has no formal precedence table. It builds many operator chains from the right. Use parentheses for simple arithmetic, but do not expect full C expression behavior.

Multiplication and division names exist in the abstract syntax tree. The parser and backend do not implement them.

Address-of code generation supports identifiers only. It treats every identifier as a FishAsm label, which is incorrect for local variables.

Dereference expressions parse but have no FishAsm backend case.

Comparison code generation sets machine flags. It does not produce a Boolean value in a register.

## Conditional statements

### If statement

An `if` body must use braces.

```c
if (left == right) {
	return;
} else if (left < right) {
	return;
} else {
	return;
}
```

The FishAsm backend requires a comparison expression as the condition. Simple Boolean conditions do not work.

```c
if (true) {       // parser failure
}

if (ready) {      // parser failure
}
```

Current equality and relational branch generation is incorrect. Do not rely on `if` output until the control-flow backend is repaired.

### While statement

A `while` body must use braces.

```c
while (index < count) {
	index++;
}
```

The backend supports comparison conditions and the literal `true`. Other condition forms are not implemented.

### Break and continue

```c
break;
continue;
```

The parser accepts both statements. The current backend mishandles nested control flow and loop-label cleanup.

## Increment and decrement

Postfix increment and decrement work only as identifier statements.

```c
index++;
index--;
```

They are not general expressions. Prefix forms and indexed forms are not supported.

## Classes

The parser accepts this experimental class form:

```c
class Example {
	int value;

	__ctor() {
	}

	__dtor() {
	}

	void reset() {
		value = 0;
	}
}
```

The parser renames constructors and destructors. It also adds a hidden `this` parameter to each method.

The language has no member-access operator, object allocation, object layout, or method-call syntax. The FishAsm backend emits C-style structure text instead of valid FishAsm data.

Classes are syntax experiments only.

## Unsupported C syntax

The standalone compiler does not support these common C features:

- `for`, `do`, and `switch`
- `struct`, `enum`, `union`, and `typedef`
- Member access with `.` or `->`
- Casts and `sizeof`
- Function calls inside expressions
- Function pointers and indirect calls
- Unary negation and logical negation
- Multiplication, division, and modulo
- Logical, bitwise, and shift operators
- The conditional operator
- Initializer lists
- Preprocessor directives and include files
- `const`, `volatile`, and storage-class qualifiers

## Diagnostics

The compiler throws general exceptions for most syntax and code-generation errors. Some errors include a token position, but many do not.

The parser also prints five look-ahead tokens during normal compilation. This output is unconditional debug output.
