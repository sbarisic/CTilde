uint* int_table;
string temp_buffer = static string[50];
int input_length = 50;
int input_count = 0;

void print(string str) {
	uint i = 0;

	while (str[i] != 0) {
		syscall_2(1, str[i]);
		i++;
	}
}

void input_add(char c) {
	print("Add char\n");
	syscall_2(2, c);

	if (c == '\b') {
		if (input_count > 0) {
			input_count--;
			temp_buffer[input_count] = 0;
			return;
		}
	}

	// Only add if there is space for at least one more character (for newline)
	if (input_count < input_length - 1) {
		print("Add char actually\n");

		temp_buffer[input_count] = c;
		input_count++;
		syscall_2(1, c);
	}
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
	int_table[0] = &handler_int0;
	int_table[1] = &handler_int1_keyboardkey;
	int_table[2] = &handler_int2_keyboardchar;

	//print("Hello, World!\n");

	input_add('A');
	//input_add('B');
	//input_add('C');
	//while (true) {
	//}

	__asm("SYSCALL $0");
}