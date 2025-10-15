uint* int_table;
string temp_buffer = static string[50];
string temp_buffer2 = static string[50];
int input_length = 50;
int input_count = 0;

void print(string str) {
	uint i = 0;

	while (str[i] != 0) {
		syscall_2(1, str[i]);
		i++;
	}
}

void printnum(uint num) {
	syscall_2(2, num);
}

string tmp_chr = static string[2];

void printchar(char c) {
	tmp_chr[0] = c;
	tmp_chr[1] = 0;
	print(tmp_chr);
}

void memory_copy(string dest, string source, int len) {
	uint i = 0;
	char tmp = 0;

	while (i < len) {
		tmp = source[i];
		dest[i] = tmp;
		i++;
	}
}

void memory_clear(string dest, int len) {
	uint i = 0;

	print("clear ");
	printnum(dest);

	print("\nlen ");
	printnum(len);
	print("\n");

	while (i < len) {
		dest[i] = 0;
		i++;
	}
}

void input_add(char c) {
	//print("AC\n");
	//syscall_2(2, c);

	if (c == '\b') {
		if (input_count > 0) {
			input_count--;
			temp_buffer[input_count] = 0;
			return;
		}
	}

	// Only add if there is space for at least one more character (for newline)
	if (input_count < input_length - 1) {
		temp_buffer[input_count] = c;
		input_count++;
		printchar(c);
	}
}

int input_readline(string dst) {
	int ret = 0;
	int idx = 0;
	char chr = 0;
	dst[0] = 0;

	while (true) {
		if (input_count == 0) {
			continue;
		}

		idx = input_count - 1;
		chr = temp_buffer[idx];
		printchar('\n');
		print("- ");
		printchar(chr);
		printchar('\n');

		if (chr == '\n') {
			memory_copy(dst, temp_buffer, idx);
			ret = input_count - 1;
			dst[ret] = 0;
			//memory_clear(input_array, input_count);
			input_count = 0;
			temp_buffer[0] = 0;

			__asm("DBG_BREAK");
			return ret;
		}
	}

	return ret;
}

void handler_int0() {
	print("INT0\n");
}

void handler_int1_keyboardkey(uint key) {
}

void handler_int2_keyboardchar(uint key2) {
	input_add(key2);
}

void kmain() {
	//int_table[0] = &handler_int0;
	//int_table[1] = &handler_int1_keyboardkey;
	//int_table[2] = &handler_int2_keyboardchar;

	//print("Hello, Universe!\n");
	input_add('H');
	//input_add('e');
	//input_add('l');
	//input_add('l');
	//input_add('o');
	//input_add(' ');
	//input_add('W');
	//input_add('o');
	//input_add('r');
	//input_add('l');
	//input_add('d');
	input_add('\n');

	/*while (true) {
		print("In: ");
		input_readline(temp_buffer2);
		print("You typed: ");
		print(temp_buffer2);
		print("\n");

		__asm("WAIT");
	}*/

	__asm("SYSCALL $0");
}